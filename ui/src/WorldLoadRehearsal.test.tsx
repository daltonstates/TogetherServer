import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { parseWorldLoadView, WorldLoadRehearsalPanel } from './WorldLoadRehearsal'

const view = () => ({ id: 'b7dc82fb-256f-45f9-962a-8039f5e0b5ab', game: 'Terraria', sourceKind: 'Received',
  copyIdentity: 'A'.repeat(64), appVersion: '0.3.0', gameVersion: 'Unknown', state: 'Prepared', canLaunch: false,
  launchReason: 'Manual isolated setup required.', gamePort: 35000, worldDirectory: 'G:\\disposable', rehearsalProfileId: null,
  loadOutcome: 'Unobserved', changeOutcome: 'Unobserved', restartOutcome: 'Unobserved', managedStarts: 0,
  gracefulStops: 0, cleaned: false, preparedUtc: '2026-10-04T12:00:00Z' })
afterEach(() => vi.unstubAllGlobals())
describe('guided world load', () => {
  it('keeps the full exact copy identity and working folder available without changing the rehearsal', async () => {
    const source = { ...view(), worldDirectory: `C:\\disposable\\${'synthetic-working-copy-'.repeat(20)}\\world` }
    const fetch = vi.fn(async () => new Response(JSON.stringify({ ok: true, message: '', rehearsals: [source] })))
    vi.stubGlobal('fetch', fetch)
    render(<WorldLoadRehearsalPanel profileId="test-profile" received />)
    fireEvent.click(screen.getByText('Test a copy in the game'))
    await screen.findByRole('region', { name: 'World load steps' })
    fireEvent.click(screen.getByText('Copy and setup details'))
    expect(screen.getByText(source.copyIdentity)).toBeVisible()
    expect(screen.getByText(`Working folder: ${source.worldDirectory}`)).toBeVisible()
    expect(screen.getByRole('button', { name: 'The change survived restart' })).toBeDisabled()
    expect(fetch).not.toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ method: 'POST' }))
  })

  it('requires a dialog decision before stopped cleanup and retains the original source on decline', async () => {
    const source = view()
    const fetch = vi.fn(async (path: string) => new Response(JSON.stringify(path.endsWith('/cleanup') ?
      { ok: true, code: 'WorldLoadCleaned', message: 'Original source retained.', rehearsal: { ...source, cleaned: true } } :
      { ok: true, message: '', rehearsals: [source] })))
    vi.stubGlobal('fetch', fetch)
    render(<WorldLoadRehearsalPanel profileId="test-profile" received />)
    fireEvent.click(screen.getByRole('button', { name: 'Refresh results' }))
    const opener = await screen.findByRole('button', { name: 'Delete this test copy' })
    opener.focus()
    fireEvent.click(opener)
    const dialog = screen.getByRole('dialog', { name: 'Delete test copy?' })
    expect(dialog).toHaveAccessibleDescription('Confirm the test game is stopped. Delete this test copy? The original save and test results will be kept.')
    expect(within(dialog).getByRole('button', { name: 'Cancel' })).toHaveFocus()
    expect(fetch.mock.calls.filter(([path]) => path.endsWith('/cleanup'))).toHaveLength(0)
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(opener).toHaveFocus()
    expect(fetch.mock.calls.filter(([path]) => path.endsWith('/cleanup'))).toHaveLength(0)
    expect(screen.getByText(/Copy:/)).toHaveTextContent(source.copyIdentity)
    fireEvent.click(opener)
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Delete test copy' }))
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Original source retained.'))
    expect(fetch).toHaveBeenCalledWith(`/api/local/world-load/${source.id}/cleanup`, expect.objectContaining({
      method: 'POST', body: JSON.stringify({ confirmStopped: true })
    }))
  })

  it('does not clean up a changed selection, profile or unmounted panel after opening the dialog', async () => {
    const first = view(), second = { ...view(), id: 'a7dc82fb-256f-45f9-962a-8039f5e0b5ab', copyIdentity: 'B'.repeat(64) }
    const fetch = vi.fn(async () => new Response(JSON.stringify({ ok: true, message: '', rehearsals: [first, second] })))
    vi.stubGlobal('fetch', fetch)
    const rendered = render(<WorldLoadRehearsalPanel profileId="test-profile" received />)
    fireEvent.click(screen.getByRole('button', { name: 'Refresh results' }))
    await screen.findByRole('button', { name: 'Delete this test copy' })
    fireEvent.click(screen.getByRole('button', { name: 'Delete this test copy' }))
    fireEvent.change(screen.getByRole('combobox'), { target: { value: second.id } })
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Delete test copy' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: 'Delete this test copy' }))
    rendered.rerender(<WorldLoadRehearsalPanel profileId="other-profile" received />)
    fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Delete test copy' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    fireEvent.click(screen.getByRole('button', { name: 'Delete this test copy' }))
    rendered.unmount()
    await Promise.resolve()
    expect(fetch.mock.calls).toHaveLength(1)
  })

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
    fireEvent.click(screen.getByRole('button', { name: 'Prepare test copy' }))
    await waitFor(() => expect(screen.getByText(/To test manually,/)).toBeInTheDocument())
    expect(screen.queryByRole('button', { name: 'Start test copy' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Stop test copy' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'The change survived restart' })).toBeDisabled()
    expect(fetch).toHaveBeenCalledWith('/api/local/friend/test-profile/world-load/prepare', expect.objectContaining({ method: 'POST', body: undefined }))
    fireEvent.click(within(screen.getByRole('status')).getByRole('button', { name: 'Dismiss notification' }))
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
    expect(screen.getByText(/Copy:/)).toHaveTextContent(view().copyIdentity)
    expect(screen.getByRole('button', { name: 'The change survived restart' })).toBeDisabled()
    expect(fetch).toHaveBeenCalledTimes(1)
    fireEvent.click(screen.getByRole('button', { name: 'Prepare test copy' }))
    expect(await screen.findByRole('status')).toHaveTextContent('Disposable copy prepared.')
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
    await waitFor(() => expect(screen.getByRole('button', { name: 'Stop test copy' })).toBeEnabled())
    expect(screen.queryByRole('button', { name: 'Start test copy' })).not.toBeInTheDocument()
    expect(screen.queryByText(/To test manually,/)).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Stop test copy' }))
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent('Exact disposable process stopped.'))
    expect(fetch).toHaveBeenCalledWith(`/api/local/world-load/${changed.id}/stop`, expect.objectContaining({
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-TogetherServer-Local': '1' }, body: undefined
    }))
  })
  it('requires a completed backup and explicit stopped cleanup', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ ok: true, message: '', rehearsals: [] }))))
    render(<WorldLoadRehearsalPanel profileId="test-profile" />)
    expect(screen.getByRole('button', { name: 'Prepare test copy' })).toBeDisabled()
    expect(screen.getByText('Show completed backups first')).toBeInTheDocument()
  })
})

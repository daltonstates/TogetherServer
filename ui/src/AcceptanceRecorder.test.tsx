import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { AcceptanceRecorder, acceptanceExplanationReport, staleAcceptanceExplanation,
  type AcceptanceIdentityField, type AcceptanceLoader, type AcceptanceSaver } from './AcceptanceRecorder'
import type { AcceptanceResult, AcceptanceView } from './contracts'

const profileId = '11111111-1111-4111-8111-111111111111'
const otherId = '99999999-9999-4999-8999-999999999999'
const view: AcceptanceView = { profileId, stale: true, gameFilesAvailable: true, gameFilesChanged: false,
  updatedUtc: '2026-10-07T12:00:00Z', evidenceBoundary: 'Owner confirmations are not automatic proof.',
  checks: [{ id: 'RealJoin', label: 'Real game join', evidence: 'The intended Friend reached the game world.',
    confirmed: false, confirmedUtc: null }] }

afterEach(() => vi.restoreAllMocks())

describe('AcceptanceRecorder stale evidence', () => {
  it('names the changed identity and admits that the exact configuration/version cause was not recorded', async () => {
    render(<AcceptanceRecorder profileId={profileId} loader={async () => view} />)
    expect(await screen.findByText(/server or Friend connection settings differ/)).toBeInTheDocument()
    expect(screen.getByText(/does not show which setting changed/)).toBeInTheDocument()
    expect(screen.getByText(/a version change cannot be confirmed/)).toBeInTheDocument()
    expect(screen.getByRole('checkbox')).not.toBeChecked()
  })

  it('dismisses stale evidence without confirming any check and restores the warning for a new server', async () => {
    const loader: AcceptanceLoader = async id => ({ ...view, profileId: id })
    const saver = vi.fn<AcceptanceSaver>()
    const display = render(<AcceptanceRecorder profileId={profileId} loader={loader} saver={saver} />)
    await screen.findByText('Server setup changed')
    fireEvent.click(within(screen.getByRole('status')).getByRole('button', { name: 'Dismiss notification' }))
    expect(screen.queryByText('Server setup changed')).not.toBeInTheDocument()
    expect(screen.getByRole('checkbox')).not.toBeChecked()
    expect(saver).not.toHaveBeenCalled()
    display.rerender(<AcceptanceRecorder profileId={profileId} loader={loader} saver={saver} />)
    expect(screen.queryByText('Server setup changed')).not.toBeInTheDocument()
    display.rerender(<AcceptanceRecorder profileId={otherId} loader={loader} saver={saver} />)
    expect(await screen.findByText('Server setup changed')).toBeInTheDocument()
    expect(screen.getByRole('checkbox')).not.toBeChecked()
    expect(saver).not.toHaveBeenCalled()
  })

  it('distinguishes readable changed files from unavailable files without inventing a game update', () => {
    const changed = staleAcceptanceExplanation({ ...view, gameFilesChanged: true })
    expect(changed.join(' ')).toContain('game server app no longer matches')
    const missing = staleAcceptanceExplanation({ ...view, gameFilesAvailable: false, gameFilesChanged: true })
    expect(missing.join(' ')).toContain('This does not confirm a game update')
    expect(missing.join(' ')).not.toContain('game server app no longer matches')
  })

  it('uses supplied compared fields and labeled version evidence only for the current profile', () => {
    const evidence = { profileId, changedFields: ['ports', 'route'] as const,
      recordedGameVersion: '0.220.4', currentGameVersion: '0.220.5', versionSource: 'observed' as const }
    const current = staleAcceptanceExplanation(view, evidence).join(' ')
    expect(current).toContain('Changed settings: ports, Friend connection')
    expect(current).toContain('Observed game versions: recorded 0.220.4; current 0.220.5')
    const wrongScope = staleAcceptanceExplanation(view, { ...evidence, profileId: otherId }).join(' ')
    expect(wrongScope).not.toContain('0.220.4')
    expect(wrongScope).not.toContain('Changed settings')
  })

  it('copies only fixed facts and excludes labels, names, IDs, fingerprints, endpoint/path strings and secret versions', async () => {
    const privateText = 'PRIVATE_NAME password=PRIVATE_PASSWORD https://192.0.2.22 C:\\private\\world'
    const tainted = { ...view, evidenceBoundary: privateText,
      checks: [{ ...view.checks[0], label: privateText, evidence: privateText }] }
    const writeText = vi.fn<(text: string) => Promise<void>>().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
    const evidence = { profileId, changedFields: ['route', privateText as AcceptanceIdentityField] as const,
      recordedGameVersion: 'PRIVATE_PASSWORD', currentGameVersion: 'a'.repeat(64), versionSource: 'ownerReported' as const }
    render(<AcceptanceRecorder profileId={profileId} loader={async () => tainted} configurationEvidence={evidence} />)
    fireEvent.click(await screen.findByRole('button', { name: 'Copy explanation' }))
    await screen.findByText('Explanation copied. Private details omitted.')
    const copied = writeText.mock.calls[0][0] as string
    expect(copied).toBe(acceptanceExplanationReport(tainted, evidence))
    expect(copied).toContain('repeat the earlier tests')
    expect(copied).not.toMatch(/PRIVATE_|192\.0\.2|C:\\|11111111|a{64}/u)
    expect(new TextEncoder().encode(copied).length).toBeLessThan(2048)
    fireEvent.click(within(screen.getByText('Explanation copied. Private details omitted.')).getByRole('button', { name: 'Dismiss notification' }))
    expect(screen.queryByText('Explanation copied. Private details omitted.')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Copy explanation' }))
    expect(await screen.findByText('Explanation copied. Private details omitted.')).toBeInTheDocument()
    expect(writeText).toHaveBeenCalledTimes(2)
  })

  it('aborts a pending confirmation and ignores its result after a profile switch', async () => {
    let resolveSave!: (result: AcceptanceResult) => void
    let saveSignal: AbortSignal | undefined
    const loader: AcceptanceLoader = async id => ({ ...view, profileId: id })
    const saver: AcceptanceSaver = (_profile, _check, _confirmed, signal) => {
      saveSignal = signal
      return new Promise(resolve => { resolveSave = resolve })
    }
    const display = render(<AcceptanceRecorder profileId={profileId} loader={loader} saver={saver} />)
    fireEvent.click(await screen.findByRole('checkbox'))
    display.rerender(<AcceptanceRecorder profileId={otherId} loader={loader} saver={saver} />)
    expect(saveSignal?.aborted).toBe(true)
    await act(async () => resolveSave({ ok: true, code: 'AcceptanceRecorded', message: 'Recorded',
      view: { ...view, stale: false, checks: [{ ...view.checks[0], confirmed: true }] } }))
    await waitFor(() => expect(screen.getByRole('checkbox')).toBeEnabled())
    expect(screen.getByRole('checkbox')).not.toBeChecked()
  })

  it('ignores a late read and rejects a success response for another profile', async () => {
    let oldResolve!: (result: AcceptanceView) => void
    let oldSignal: AbortSignal | undefined
    const loader: AcceptanceLoader = (id, signal) => id === profileId ? new Promise(resolve => {
      oldResolve = resolve; oldSignal = signal
    }) : Promise.resolve({ ...view, profileId: otherId })
    const display = render(<AcceptanceRecorder profileId={profileId} loader={loader} />)
    display.rerender(<AcceptanceRecorder profileId={otherId} loader={loader} />)
    expect(oldSignal?.aborted).toBe(true)
    await act(async () => oldResolve({ ...view, checks: [] }))
    expect(await screen.findByRole('checkbox')).toBeInTheDocument()
    display.rerender(<AcceptanceRecorder profileId={profileId} loader={async () => ({ ...view, profileId: otherId })} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('different server')
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument()
  })

  it('keeps raw save failures out of owner feedback and rejects a successful result for another profile', async () => {
    const saver = vi.fn<AcceptanceSaver>()
      .mockRejectedValueOnce(new Error('PRIVATE_PASSWORD C:\\private'))
      .mockResolvedValueOnce({ ok: true, code: 'AcceptanceRecorded', message: 'PRIVATE_PASSWORD',
        view: { ...view, profileId: otherId } })
    render(<AcceptanceRecorder profileId={profileId} loader={async () => view} saver={saver} />)
    fireEvent.click(await screen.findByRole('checkbox'))
    expect(await screen.findByRole('alert')).toHaveTextContent('The owner confirmation could not be saved. Refresh')
    expect(screen.queryByText(/PRIVATE_PASSWORD/)).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('checkbox'))
    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('saved for this server'))
    expect(screen.getByRole('checkbox')).not.toBeChecked()
  })

  it('does not load when hidden and keeps raw read failures out of owner feedback', async () => {
    const loader = vi.fn(async () => view)
    const display = render(<AcceptanceRecorder profileId={profileId} visible={false} loader={loader} />)
    expect(loader).not.toHaveBeenCalled()
    display.rerender(<AcceptanceRecorder profileId={profileId} loader={async () => { throw new Error('PRIVATE_PASSWORD C:\\private') }} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Could not read recorded owner checks')
    expect(screen.queryByText(/PRIVATE_PASSWORD/)).not.toBeInTheDocument()
  })
})

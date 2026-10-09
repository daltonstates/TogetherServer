import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { WeeklySummary } from './WeeklySummary'
import { parseWeeklyServerSummary, type WeeklyServerSummaryResult } from './weeklySummaryWire'
import { weeklySummaryFixture } from './test/weeklySummaryFixture'

const profileId = '11111111-1111-4111-8111-111111111111'
const result: WeeklyServerSummaryResult = {
  ok: true, code: 'WeeklyServerSummary', message: 'Seven days of retained session evidence on this Host.', profileId,
  summary: { windowStartUtc: '2026-10-01T15:00:00Z', windowEndUtc: '2026-10-08T15:00:00Z',
    recordedRuntimeSeconds: 7620, archivedSessionCount: 5, completedSessionCount: 4, failedStarts: 1,
    unexpectedExits: 1, rollingBackupsCompleted: 1, rollingBackupsFailed: 1, rollingBackupsNotConfigured: 0,
    rollingBackupsUnsupported: 0, rollingBackupsNotAttempted: 2, peakTrustedOnlinePlayers: 4,
    sessionsWithoutTrustedCounts: 1, unavailableSessionCount: 1, clippedSessionCount: 1,
    overlappingSessionCount: 1, unfinishedRunCount: 1, undatedArchiveRecordCount: 1, archiveLimitReached: true }
}

describe('WeeklySummary', () => {
  it('opens the retained sessions contributing to each metric and excludes the clipped player peak', async () => {
    const recorded = weeklySummaryFixture()
    const loader = vi.fn(async () => recorded)
    render(<WeeklySummary profileId={profileId} visible loader={loader} />)
    fireEvent.click(await screen.findByRole('button', { name: /^Unexpected exits: 1/ }))
    expect(screen.getByText('Unexpected exit', { selector: 'strong' })).toBeInTheDocument()
    expect(screen.queryByText('Stopped gracefully', { selector: 'strong' })).not.toBeInTheDocument()
    expect(screen.getByText(/^1 of 5 retained sessions match/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /^Trusted player peak: 4/ }))
    expect(screen.getByText('Last 0 · peak 4')).toBeInTheDocument()
    expect(screen.queryByText('Last 0 · peak 99')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: /^Rolling backups: 1 completed/ }))
    expect(screen.getByText(/^2 of 5 retained sessions match/)).toBeInTheDocument()
    expect(screen.getAllByText('Stopped gracefully', { selector: 'strong' })).toHaveLength(2)
    fireEvent.change(screen.getByLabelText('Session evidence'), { target: { value: 'backupFailed' } })
    expect(screen.getByText('Last 0 · peak 0')).toBeInTheDocument()
    expect(screen.queryByText('Last 0 · peak 99')).not.toBeInTheDocument()
    expect(loader).toHaveBeenCalledOnce()
  })

  it('charts actual daily intervals and opens a UTC day while leaving days without timing unavailable', async () => {
    render(<WeeklySummary profileId={profileId} visible loader={async () => weeklySummaryFixture()} />)
    const day = await screen.findByRole('button', { name: 'Show recorded sessions on 2026-10-01 (UTC)' })
    expect(screen.getByRole('img', { name: '2026-10-01: 2h 0m recorded runtime' })).toBeInTheDocument()
    expect(screen.getByRole('img', { name: '2026-10-08: 7m recorded runtime' })).toBeInTheDocument()
    expect(screen.getAllByText('No timed evidence')).toHaveLength(6)
    expect(screen.getByRole('button', { name: 'Show recorded sessions on 2026-10-07 (UTC)' })).toBeDisabled()
    expect(screen.queryByRole('img', { name: /2026-10-07/ })).not.toBeInTheDocument()
    fireEvent.click(day)
    expect(screen.getByLabelText('Sessions from date (UTC)')).toHaveValue('2026-10-01')
    expect(screen.getByLabelText('Sessions through date (UTC)')).toHaveValue('2026-10-01')
    expect(screen.getByText(/^2 of 5 retained sessions match/)).toBeInTheDocument()
    expect(screen.queryByText('Failed before Ready', { selector: 'strong' })).not.toBeInTheDocument()
  })

  it('shows bounded recorded metrics and explicit legacy, unfinished, clipped and retained coverage', async () => {
    const { container } = render(<WeeklySummary profileId={profileId} visible loader={async () => result} />)
    expect(await screen.findByText('2h 7m')).toBeInTheDocument()
    expect(screen.getByText('1 completed · 1 failed')).toBeInTheDocument()
    fireEvent.click(screen.getByText('Coverage and missing evidence'))
    expect(screen.getByText(/1 unfinished run excluded/)).toBeInTheDocument()
    expect(screen.getByText(/1 legacy or incomplete session/)).toBeInTheDocument()
    expect(screen.getByText(/untimed player peaks are excluded/)).toBeInTheDocument()
    expect(screen.getByText(/overlapping saved interval counted once/)).toBeInTheDocument()
    expect(screen.getByText(/500-record archive limit was reached/)).toBeInTheDocument()
    expect(screen.getByText(/do not identify players or prove/)).toBeInTheDocument()
    expect(container.querySelector('.good')).not.toBeInTheDocument()
  })

  it('distinguishes an empty archive and an unavailable player peak without inferring missing history as zero', async () => {
    const empty = { ...result, summary: { ...result.summary!, recordedRuntimeSeconds: 0, archivedSessionCount: 0,
      completedSessionCount: 0, failedStarts: 0, unexpectedExits: 0, rollingBackupsCompleted: 0,
      rollingBackupsFailed: 0, rollingBackupsNotAttempted: 0, peakTrustedOnlinePlayers: null,
      sessionsWithoutTrustedCounts: 0, unavailableSessionCount: 0, clippedSessionCount: 0,
      overlappingSessionCount: 0, unfinishedRunCount: 1, archiveLimitReached: false } }
    render(<WeeklySummary profileId={profileId} visible loader={async () => empty} />)
    expect(await screen.findByText('No completed session evidence in these seven days.')).toBeInTheDocument()
    expect(screen.getByText('Unavailable')).toBeInTheDocument()
    expect(screen.getByText(/Missing history is not inferred as zero/)).toBeInTheDocument()
  })

  it('does not request a hidden summary and aborts a superseded request', async () => {
    let signal: AbortSignal | undefined
    const loader = vi.fn((_profile: string, current?: AbortSignal) => { signal = current; return new Promise<WeeklyServerSummaryResult>(() => {}) })
    const { rerender } = render(<WeeklySummary profileId={profileId} visible={false} loader={loader} />)
    expect(loader).not.toHaveBeenCalled()
    rerender(<WeeklySummary profileId={profileId} visible loader={loader} />)
    expect(screen.getByText('Loading seven-day summary…')).toBeInTheDocument()
    expect(signal?.aborted).toBe(false)
    rerender(<WeeklySummary profileId={profileId} visible={false} loader={loader} />)
    await waitFor(() => expect(signal?.aborted).toBe(true))
  })

  it('uses a fixed retryable error without exposing raw data and rejects another profile', async () => {
    const loader = vi.fn(async () => { throw new Error('C:\\private\\world password=secret') })
    render(<WeeklySummary profileId={profileId} visible loader={loader} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Seven-day summary unavailable')
    expect(screen.queryByText(/password=secret|C:\\private/)).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Try again' }))
    await waitFor(() => expect(loader).toHaveBeenCalledTimes(2))
  })
})

describe('weekly summary wire boundary', () => {
  it('accepts a strict bounded summary without raw session or identity fields', () => {
    expect(parseWeeklyServerSummary(result)).toEqual(result)
    expect(parseWeeklyServerSummary({ ok: false, code: 'UnknownProfile', message: 'Choose a saved Host server.', profileId, summary: null }).ok).toBe(false)
  })

  it('rejects unknown fields, inconsistent totals, wrong windows, unavailable success and unsafe numeric evidence', () => {
    for (const bad of [
      { ...result, summary: null },
      { ...result, summary: { ...result.summary, recordedRuntimeSeconds: 604801 } },
      { ...result, summary: { ...result.summary, windowStartUtc: '2026-10-02T15:00:00Z' } },
      { ...result, summary: { ...result.summary, completedSessionCount: 5000 } },
      { ...result, summary: { ...result.summary, rollingBackupsCompleted: 4 } },
      { ...result, summary: { ...result.summary, peakTrustedOnlinePlayers: -1 } },
      { ...result, summary: { ...result.summary, recordedRuntimeSeconds: Number.NaN } },
      { ...result, summary: { ...result.summary, unfinishedRunCount: -1 } },
      { ...result, summary: { ...result.summary, playerNames: ['private identity'] } },
      { ...result, currentRawLog: 'private log' }
    ]) expect(() => parseWeeklyServerSummary(bad)).toThrow()
  })
})

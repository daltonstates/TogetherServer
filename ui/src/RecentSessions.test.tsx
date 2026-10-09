import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { emptySessionFilters, filterSessionHistory, RecentSessions } from './RecentSessions'
import type { RecentServerSessionsResult } from './contracts'
import { weeklySummaryFixture } from './test/weeklySummaryFixture'

const profileId = '11111111-1111-4111-8111-111111111111'
const empty: RecentServerSessionsResult = {
  ok: true,
  code: 'RecentSessions',
  message: 'No archived sessions are available for this server.',
  profileId,
  sessions: []
}

describe('RecentSessions', () => {
  it('includes known sub-millisecond runtime in the day drill-down and excludes an interval ending at the day boundary', () => {
    const summary = weeklySummaryFixture().summary!
    const source = summary.sessions![3]
    const tiny = { ...source, session: { ...source.session, startedUtc: '2026-10-08T14:59:59.9999999Z',
      endedUtc: '2026-10-08T15:00:00.0000000Z', durationSeconds: 0 } }
    expect(filterSessionHistory([tiny], { ...emptySessionFilters, metric: 'runtime',
      fromDate: '2026-10-08', throughDate: '2026-10-08' }, summary)).toHaveLength(1)
    const boundary = { ...source, session: { ...source.session, startedUtc: '2026-10-07T23:00:00Z',
      endedUtc: '2026-10-08T00:00:00Z', durationSeconds: 3600 } }
    expect(filterSessionHistory([boundary], { ...emptySessionFilters, fromDate: '2026-10-08', throughDate: '2026-10-08' }, summary)).toHaveLength(0)
  })

  it('filters outcomes and UTC dates, includes cross-midnight intervals, and leaves undated legacy evidence unplaced', async () => {
    const entries = weeklySummaryFixture().summary!.sessions!
    const crossing = { ...entries[0], session: { ...entries[0].session,
      startedUtc: '2026-10-06T23:30:00Z', endedUtc: '2026-10-07T00:30:00Z', durationSeconds: 3600 } }
    expect(filterSessionHistory([crossing, entries[4]], { ...emptySessionFilters, fromDate: '2026-10-07', throughDate: '2026-10-07' })).toHaveLength(2)
    expect(filterSessionHistory([{ session: entries[4].session }], { ...emptySessionFilters, fromDate: '2026-10-07' })).toHaveLength(0)
    render(<RecentSessions profileId={profileId} visible loader={async () => ({ ...empty, sessions: entries.map(entry => entry.session) })} />)
    await screen.findByText('Outcome unavailable', { selector: 'strong' })
    fireEvent.change(screen.getByLabelText('Session outcome'), { target: { value: 'failedStarts' } })
    expect(screen.getByText('Failed before Ready', { selector: 'strong' })).toBeInTheDocument()
    expect(screen.getByText(/^1 of 5 retained sessions match/)).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Sessions through date (UTC)'), { target: { value: '2026-10-07' } })
    expect(screen.getByText('No retained sessions match these filters.')).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Sessions from date (UTC)'), { target: { value: '2026-10-08' } })
    expect(screen.getByRole('alert')).toHaveTextContent('From date must be on or before Through date.')
    fireEvent.click(screen.getByRole('button', { name: 'Clear session filters' }))
    expect(screen.getByText(/^5 of 5 retained sessions match/)).toBeInTheDocument()
  })

  it('ignores an old manual refresh after the selected profile changes and rejects a mismatched response', async () => {
    let oldResolve!: (result: RecentServerSessionsResult) => void
    let oldSignal: AbortSignal | undefined
    const otherId = '99999999-9999-4999-8999-999999999999'
    let loads = 0
    const loader = vi.fn((_profile: string, signal?: AbortSignal) => {
      loads++
      if (loads === 1) return Promise.resolve(empty)
      if (loads === 2) { oldSignal = signal; return new Promise<RecentServerSessionsResult>(resolve => { oldResolve = resolve }) }
      return Promise.resolve({ ...empty, profileId: otherId })
    })
    const view = render(<RecentSessions profileId={profileId} visible loader={loader} />)
    await screen.findByText('No archived sessions yet')
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }))
    view.rerender(<RecentSessions profileId={otherId} visible loader={loader} />)
    expect(oldSignal?.aborted).toBe(true)
    await act(async () => oldResolve({ ...empty, sessions: weeklySummaryFixture().summary!.sessions!.map(entry => entry.session) }))
    expect(await screen.findByText('No archived sessions yet')).toBeInTheDocument()
    expect(screen.queryByText('Stopped gracefully')).not.toBeInTheDocument()
    view.rerender(<RecentSessions profileId={profileId} visible loader={async () => ({ ...empty, profileId: otherId })} />)
    expect(await screen.findByRole('alert')).toHaveTextContent('Recent sessions unavailable')
  })

  it('shows a compact loading state and then an empty state', async () => {
    let resolve!: (value: RecentServerSessionsResult) => void
    const loader = vi.fn(() => new Promise<RecentServerSessionsResult>(done => { resolve = done }))
    render(<RecentSessions profileId={profileId} visible loader={loader} />)

    expect(screen.getByText('Loading recent sessions…')).toBeInTheDocument()
    resolve(empty)
    expect(await screen.findByText('No archived sessions yet')).toBeInTheDocument()
    expect(loader).toHaveBeenCalledWith(profileId, expect.any(AbortSignal))
  })

  it('shows complete and legacy evidence without green positive styling', async () => {
    const result: RecentServerSessionsResult = {
      ...empty,
      message: 'Showing 2 recent archived sessions.',
      sessions: [{
        profileId,
        operationId: '22222222-2222-4222-8222-222222222222',
        gameKind: 'Valheim',
        startedUtc: '2026-09-28T12:00:00Z',
        endedUtc: '2026-09-28T13:01:30Z',
        durationSeconds: 3690,
        readyEverObserved: true,
        endReason: 'GracefulStop',
        outcome: 'GracefulStop',
        crashRecoveryScheduled: false,
        lastTrustedOnlinePlayers: 0,
        maximumTrustedOnlinePlayers: 4,
        backupResult: 'Completed'
      }, {
        profileId,
        operationId: '33333333-3333-4333-8333-333333333333',
        gameKind: 'MinecraftJava',
        startedUtc: null,
        endedUtc: null,
        durationSeconds: null,
        readyEverObserved: null,
        endReason: null,
        outcome: null,
        crashRecoveryScheduled: null,
        lastTrustedOnlinePlayers: null,
        maximumTrustedOnlinePlayers: null,
        backupResult: null
      }]
    }
    const { container } = render(<RecentSessions profileId={profileId} visible loader={async () => result} />)

    expect(await screen.findByText('Stopped gracefully', { selector: 'strong' })).toBeInTheDocument()
    expect(screen.getByText('Last 0 · peak 4')).toBeInTheDocument()
    expect(screen.getByText('Outcome unavailable', { selector: 'strong' })).toBeInTheDocument()
    expect(screen.getAllByText('Unavailable').length).toBeGreaterThan(1)
    expect(screen.getByText(/do not show who joined/)).toBeInTheDocument()
    expect(container.querySelector('.good')).not.toBeInTheDocument()
    expect(container.querySelector('.tone-neutral')).toBeInTheDocument()
  })

  it('uses a bounded generic error and never reflects private failure details', async () => {
    render(<RecentSessions profileId={profileId} visible loader={async () => {
      throw new Error('C:\\Users\\Owner\\world password=hunter2')
    }} />)

    expect(await screen.findByRole('alert')).toHaveTextContent('Recent sessions unavailable')
    expect(screen.queryByText(/hunter2|Users\\Owner/)).not.toBeInTheDocument()
  })

  it('does not load while its server tab is hidden', async () => {
    const loader = vi.fn(async () => empty)
    render(<RecentSessions profileId={profileId} visible={false} loader={loader} />)

    await waitFor(() => expect(loader).not.toHaveBeenCalled())
  })
})

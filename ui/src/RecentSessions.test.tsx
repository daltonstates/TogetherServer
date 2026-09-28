import { render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { RecentSessions } from './RecentSessions'
import type { RecentServerSessionsResult } from './contracts'

const profileId = '11111111-1111-4111-8111-111111111111'
const empty: RecentServerSessionsResult = {
  ok: true,
  code: 'RecentSessions',
  message: 'No archived sessions are available for this server.',
  profileId,
  sessions: []
}

describe('RecentSessions', () => {
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

    expect(await screen.findByText('Stopped gracefully')).toBeInTheDocument()
    expect(screen.getByText('Last 0 · peak 4')).toBeInTheDocument()
    expect(screen.getByText('Outcome unavailable')).toBeInTheDocument()
    expect(screen.getAllByText('Unavailable').length).toBeGreaterThan(1)
    expect(screen.getByText(/do not prove who joined/)).toBeInTheDocument()
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

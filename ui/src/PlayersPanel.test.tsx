import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { PlayersPanel } from './PlayersPanel'
import type { Run } from './contracts'

const run: Run = {
  profileId: 'server-1', state: 'Ready', detail: 'Ready', processId: 10,
  onlinePlayers: 2, maxPlayers: 10, autoShutdownAtUtc: null,
  autoShutdownReason: 'Waiting for the server to be empty.', hostAddedTime: true,
  playerNames: null, playerCountTrusted: true, friendAddedMinutes: 15,
  addedShutdownMinutes: 25, playerObservationSource: 'ValheimLogReady',
  playerCountObservedUtc: '2026-09-29T12:00:00Z'
}

describe('PlayersPanel', () => {
  it('shows trusted source, saved time split, and count-only activity', () => {
    render(<PlayersPanel run={run} nowMs={Date.parse('2026-09-29T12:00:04Z')} refreshing={false}
      disabled={false} onRefresh={() => {}} activity={[{ id: 'event-1', occurredUtc: '2026-09-29T11:59:00Z',
        category: 'Players', action: 'CountIncreased', message: 'The trusted player count increased from 1 to 2.',
        severity: 'Info', profileId: 'server-1', deviceId: null, visibility: 'AssignedFriends' }]} />)
    expect(screen.getByText('2 / 10 online')).toBeInTheDocument()
    expect(screen.getByText('Valheim query or verified server-log count')).toBeInTheDocument()
    expect(screen.getByText('10 from the Host · 15 from Friends.')).toBeInTheDocument()
    expect(screen.getByText(/does not store player identities/i)).toBeInTheDocument()
    expect(screen.getByText(/increased from 1 to 2/i)).toBeInTheDocument()
  })

  it('keeps unknown counts visibly fail-closed and refreshable', () => {
    const refresh = vi.fn()
    render(<PlayersPanel run={{ ...run, onlinePlayers: null, playerCountTrusted: false }} nowMs={0}
      refreshing={false} disabled={false} onRefresh={refresh} />)
    expect(screen.getByText('Unavailable')).toBeInTheDocument()
    expect(screen.getByText(/fail-closed/i)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Refresh count' }))
    expect(refresh).toHaveBeenCalledOnce()
  })
})

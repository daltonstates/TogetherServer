import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { PlayersPanel, playerCountAvailability } from './PlayersPanel'
import type { Run } from './contracts'

const run: Run = {
  profileId: 'server-1', state: 'Ready', detail: 'Ready', runOperationId: null, processId: 10,
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

  it('suppresses a stale zero-player countdown and gives safe next actions', () => {
    const logs = vi.fn(), health = vi.fn(), help = vi.fn()
    render(<PlayersPanel run={{ ...run, onlinePlayers: 0, autoShutdownAtUtc: '2026-09-29T12:05:00Z' }}
      nowMs={Date.parse('2026-09-29T12:00:11Z')} refreshing={false} disabled={false} onRefresh={vi.fn()}
      profile={{ kind: 'Valheim', crossplay: false }} onOpenLogs={logs} onCheckHealth={health} onOpenHelp={help} />)
    expect(screen.getByText('Unavailable')).toBeInTheDocument()
    expect(screen.getByText('Player observation is stale')).toBeInTheDocument()
    expect(screen.getByText('No empty-server countdown is active.')).toBeInTheDocument()
    expect(screen.queryByText(/deadline is in about/)).not.toBeInTheDocument()
    expect(logs).not.toHaveBeenCalled()
    expect(health).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Open logs' }))
    fireEvent.click(screen.getByRole('button', { name: 'Check server health' }))
    fireEvent.click(screen.getByRole('button', { name: 'Player-count help' }))
    expect(logs).toHaveBeenCalledOnce()
    expect(health).toHaveBeenCalledOnce()
    expect(help).toHaveBeenCalledOnce()
  })

  it('explains the Terraria driver limitation rather than suggesting another refresh will establish zero', () => {
    render(<PlayersPanel run={{ ...run, onlinePlayers: null, playerCountTrusted: false, state: 'Starting' }}
      profile={{ kind: 'Terraria', crossplay: false }} nowMs={Date.parse('2026-09-29T12:00:04Z')}
      refreshing={false} disabled={false} onRefresh={vi.fn()} />)
    expect(screen.getByText(/cannot verify readiness or report a trusted player count/)).toBeInTheDocument()
    expect(screen.getByText(/Refresh cannot remove this driver limitation/)).toBeInTheDocument()
  })

  it('rejects missing/future observation times and display-only Custom counts even when the old count is zero', () => {
    const now = Date.parse('2026-09-29T12:00:04Z')
    for (const changed of [
      { ...run, onlinePlayers: 0, playerCountObservedUtc: null },
      { ...run, onlinePlayers: 0, playerCountObservedUtc: '2026-09-29T12:01:00Z' },
      { ...run, onlinePlayers: 0, playerObservationSource: 'CustomScriptReady' },
      { ...run, onlinePlayers: -1 }, { ...run, onlinePlayers: 0, playerObservationSource: 'ObservationPending' }
    ]) expect(playerCountAvailability(changed, now).count).toBeNull()
    expect(playerCountAvailability({ ...run, onlinePlayers: 0 }, Date.parse('2026-09-29T12:00:10Z')).count).toBe(0)
  })

  it('does not display an old deadline for a positive count', () => {
    render(<PlayersPanel run={{ ...run, autoShutdownAtUtc: '2026-09-29T12:05:00Z' }}
      nowMs={Date.parse('2026-09-29T12:00:04Z')} refreshing={false} disabled={false} onRefresh={vi.fn()} />)
    expect(screen.getByText('No empty-server countdown is active.')).toBeInTheDocument()
    expect(screen.queryByText(/deadline is in about/)).not.toBeInTheDocument()
  })

  it('announces count availability politely without reading each timer/freshness tick or moving focus', () => {
    const initial = { ...run, onlinePlayers: 0, autoShutdownAtUtc: '2026-09-29T12:05:00Z' }
    const now = Date.parse('2026-09-29T12:00:04Z')
    const { rerender } = render(<PlayersPanel run={initial} nowMs={now} refreshing={false} disabled={false} onRefresh={vi.fn()} />)
    const refresh = screen.getByRole('button', { name: 'Refresh count' })
    refresh.focus()
    const announcement = screen.getByRole('status').textContent
    rerender(<PlayersPanel run={initial} nowMs={now + 1000} refreshing={false} disabled={false} onRefresh={vi.fn()} />)
    expect(screen.getByRole('status').textContent).toBe(announcement)
    expect(screen.getByRole('status')).toHaveAttribute('aria-live', 'polite')
    expect(refresh).toHaveFocus()
  })
})

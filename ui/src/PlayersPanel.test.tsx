import { fireEvent, render, screen, within } from '@testing-library/react'
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
  it('keeps the current count visible and source, freshness, driver help and count history collapsed', () => {
    render(<PlayersPanel run={{ ...run, playerNames: ['Private player name'] }} nowMs={Date.parse('2026-09-29T12:00:04Z')} refreshing={false}
      disabled={false} onRefresh={() => {}} profile={{ kind: 'Valheim', crossplay: false }} activity={[{ id: 'event-1', occurredUtc: '2026-09-29T11:59:00Z',
        category: 'Players', action: 'CountIncreased', message: 'The trusted player count increased from 1 to 2.',
        severity: 'Info', profileId: 'server-1', deviceId: null, visibility: 'AssignedFriends' }]} />)
    expect(within(screen.getByRole('group', { name: 'Players online' })).getByText('2 / 10 online')).toBeVisible()
    expect(within(screen.getByRole('group', { name: 'Empty-server timer' })).getByText('No countdown')).toBeVisible()
    for (const label of ['Count source & timer details', 'Player-count help', 'Recent count changes']) {
      const summary = screen.getByText(label, { selector: 'summary' })
      expect(summary).toBeVisible()
      expect(summary.closest('details')).not.toHaveAttribute('open')
    }
    expect(screen.getByText('Valheim query or verified server-log count')).not.toBeVisible()
    expect(screen.getByText('Observed just now.')).not.toBeVisible()
    expect(screen.getByText('10 from the Host · 15 from Friends.')).not.toBeVisible()
    expect(screen.getByText(/partial or contradictory log stays Unknown/)).not.toBeVisible()
    expect(screen.getByText(/increased from 1 to 2/i)).not.toBeVisible()
    expect(screen.queryByText('Private player name')).not.toBeInTheDocument()

    fireEvent.click(screen.getByText('Count source & timer details', { selector: 'summary' }))
    expect(screen.getByText('Valheim query or verified server-log count')).toBeVisible()
    expect(screen.getByText('Observed just now.')).toBeVisible()
    expect(screen.getByText('10 from the Host · 15 from Friends.')).toBeVisible()
    fireEvent.click(screen.getByText('Recent count changes', { selector: 'summary' }))
    expect(screen.getByText(/does not store player identities/i)).toBeVisible()
    expect(screen.getByText(/increased from 1 to 2/i)).toBeVisible()
    expect(screen.getByText(/partial or contradictory log stays Unknown/)).not.toBeVisible()
  })

  it('keeps unknown counts visibly fail-closed and refreshable', () => {
    const refresh = vi.fn()
    render(<PlayersPanel run={{ ...run, onlinePlayers: null, playerCountTrusted: false }} nowMs={0}
      refreshing={false} disabled={false} onRefresh={refresh} />)
    expect(screen.getByText('Unavailable')).toBeVisible()
    expect(screen.getByText('No countdown')).toBeVisible()
    expect(within(screen.getByRole('note')).getByText('Player count unavailable')).toBeVisible()
    expect(within(screen.getByRole('note')).getByText('Friend and automatic Stop remain blocked.')).toBeVisible()
    fireEvent.click(screen.getByRole('button', { name: 'Refresh count' }))
    expect(refresh).toHaveBeenCalledOnce()
  })

  it('suppresses a stale zero-player countdown and keeps its warning visible while help and next actions are collapsed', () => {
    const logs = vi.fn(), health = vi.fn(), help = vi.fn()
    render(<PlayersPanel run={{ ...run, onlinePlayers: 0, autoShutdownAtUtc: '2026-09-29T12:05:00Z' }}
      nowMs={Date.parse('2026-09-29T12:00:11Z')} refreshing={false} disabled={false} onRefresh={vi.fn()}
      profile={{ kind: 'Valheim', crossplay: false }} onOpenLogs={logs} onCheckHealth={health} onOpenHelp={help} />)
    expect(screen.getByText('Unavailable')).toBeVisible()
    expect(screen.getByText('Player observation is stale')).toBeVisible()
    expect(screen.getByText('No countdown')).toBeVisible()
    expect(screen.queryByText(/deadline is in about/)).not.toBeInTheDocument()
    expect(screen.queryByText(/About \d+ min/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Open logs' })).not.toBeVisible()
    expect(screen.getByRole('button', { name: 'Check server health' })).not.toBeVisible()
    expect(logs).not.toHaveBeenCalled()
    expect(health).not.toHaveBeenCalled()
    expect(help).not.toHaveBeenCalled()
    fireEvent.click(screen.getByText('Player-count help', { selector: 'summary' }))
    expect(screen.getByText(/partial or contradictory log stays Unknown/)).toBeVisible()
    expect(logs).not.toHaveBeenCalled()
    expect(health).not.toHaveBeenCalled()
    expect(help).not.toHaveBeenCalled()
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
    expect(screen.getByText('Unavailable')).toBeVisible()
    expect(within(screen.getByRole('note')).getByText('Friend and automatic Stop remain blocked.')).toBeVisible()
    expect(screen.getByText(/cannot verify readiness or report a trusted player count/)).not.toBeVisible()
    fireEvent.click(screen.getByText('Player-count help', { selector: 'summary' }))
    expect(screen.getByText(/cannot verify readiness or report a trusted player count/)).toBeVisible()
    expect(screen.getByText(/Refresh cannot remove this driver limitation/)).toBeVisible()
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
    expect(screen.getByText('No countdown')).toBeVisible()
    expect(screen.queryByText(/deadline is in about/)).not.toBeInTheDocument()
    expect(screen.queryByText(/About \d+ min/)).not.toBeInTheDocument()
  })

  it('shows the current zero count and the Host countdown without expanding any details', () => {
    render(<PlayersPanel run={{ ...run, onlinePlayers: 0, maxPlayers: null,
      autoShutdownAtUtc: '2026-09-29T12:05:00Z', autoShutdownReason: 'Empty-server countdown running.' }}
      nowMs={Date.parse('2026-09-29T12:00:04Z')} refreshing={false} disabled={false} onRefresh={vi.fn()} />)
    expect(within(screen.getByRole('group', { name: 'Players online' })).getByText('0 online')).toBeVisible()
    expect(within(screen.getByRole('group', { name: 'Empty-server timer' })).getByText('About 5 min')).toBeVisible()
    expect(screen.getByText('Empty-server countdown running.')).toBeVisible()
    expect(screen.getByText('The empty-server deadline is in about 5 minutes.')).not.toBeVisible()
    expect(screen.queryByRole('note')).not.toBeInTheDocument()
  })

  it('keeps an arrived deadline visible without claiming that Stop has happened', () => {
    render(<PlayersPanel run={{ ...run, onlinePlayers: 0, autoShutdownAtUtc: '2026-09-29T12:00:03Z' }}
      nowMs={Date.parse('2026-09-29T12:00:04Z')} refreshing={false} disabled={false} onRefresh={vi.fn()} />)
    expect(screen.getByText('Deadline reached')).toBeVisible()
    expect(screen.getByText('The Host must recheck players before Stop.')).toBeVisible()
  })

  it('shows an invalid deadline as unavailable without a numeric countdown', () => {
    render(<PlayersPanel run={{ ...run, onlinePlayers: 0, autoShutdownAtUtc: 'invalid-deadline' }}
      nowMs={Date.parse('2026-09-29T12:00:04Z')} refreshing={false} disabled={false} onRefresh={vi.fn()} />)
    expect(within(screen.getByRole('group', { name: 'Empty-server timer' })).getByText('Unavailable')).toBeVisible()
    expect(screen.getByText('A valid countdown deadline is not available.')).toBeVisible()
    expect(screen.queryByText(/About \d+ min/)).not.toBeInTheDocument()
    expect(screen.queryByText('Deadline reached')).not.toBeInTheDocument()
  })

  const unavailableZeroCases: { description: string; change: Partial<Run> }[] = [
    { description: 'missing observation time', change: { playerCountObservedUtc: null } },
    { description: 'future observation time', change: { playerCountObservedUtc: '2026-09-29T12:01:00Z' } },
    { description: 'untrusted zero', change: { playerCountTrusted: false } },
    { description: 'display-only Custom zero', change: { playerObservationSource: 'CustomScriptReady' } }
  ]

  it.each(unavailableZeroCases)('suppresses an old zero and countdown with $description', ({ change }) => {
    render(<PlayersPanel run={{ ...run, onlinePlayers: 0, autoShutdownAtUtc: '2026-09-29T12:05:00Z', ...change }}
      nowMs={Date.parse('2026-09-29T12:00:04Z')} refreshing={false} disabled={false} onRefresh={vi.fn()} />)
    expect(within(screen.getByRole('group', { name: 'Players online' })).getByText('Unavailable')).toBeVisible()
    expect(screen.getByText('No countdown')).toBeVisible()
    expect(within(screen.getByRole('note')).getByText('Friend and automatic Stop remain blocked.')).toBeVisible()
    expect(screen.queryByText('0 / 10 online')).not.toBeInTheDocument()
    expect(screen.queryByText(/About \d+ min/)).not.toBeInTheDocument()
  })

  it('announces count availability politely without reading each timer/freshness tick or moving focus', () => {
    const initial = { ...run, onlinePlayers: 0, autoShutdownAtUtc: '2026-09-29T12:05:00Z' }
    const now = Date.parse('2026-09-29T12:00:04Z')
    const { rerender } = render(<PlayersPanel run={initial} nowMs={now} refreshing={false} disabled={false} onRefresh={vi.fn()} />)
    const sourceSummary = screen.getByText('Count source & timer details', { selector: 'summary' })
    fireEvent.click(sourceSummary)
    const refresh = screen.getByRole('button', { name: 'Refresh count' })
    refresh.focus()
    const announcement = screen.getByRole('status').textContent
    rerender(<PlayersPanel run={initial} nowMs={now + 1000} refreshing={false} disabled={false} onRefresh={vi.fn()} />)
    expect(screen.getByRole('status').textContent).toBe(announcement)
    expect(screen.getByRole('status')).toHaveAttribute('aria-live', 'polite')
    expect(sourceSummary.closest('details')).toHaveAttribute('open')
    expect(screen.getByText('Observed 5 seconds ago.')).toBeVisible()
    expect(refresh).toHaveFocus()
    rerender(<PlayersPanel run={initial} nowMs={now + 7000} refreshing={false} disabled={false} onRefresh={vi.fn()} />)
    expect(screen.getByRole('status')).toHaveTextContent('Player count unavailable. Friend and automatic Stop remain blocked.')
    expect(screen.getByText('No countdown')).toBeVisible()
    expect(refresh).toHaveFocus()
  })
})

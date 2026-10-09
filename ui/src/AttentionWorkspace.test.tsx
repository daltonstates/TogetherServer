import { fireEvent, render, screen, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { ActivityEvent } from './contracts'
import {
  AttentionWorkspace, attentionHistoryStorageKey, defaultAttentionFilters, filterAttentionEvents,
  filterAttentionProblems, groupAttentionEvents, readAttentionHistory, withAttentionHistorySource,
  type AttentionHistoryState, type AttentionProblemRow, type AttentionStorage
} from './AttentionWorkspace'

const nowMs = Date.parse('2026-10-08T12:00:00Z')
const id = (number: number) => `00000000-0000-0000-0000-${number.toString(16).padStart(12, '0')}`
const servers = [{ id: 'server-1', name: 'Evening world' }, { id: 'server-2', name: 'Weekend world' }]
const event = (number = 1, changed: Partial<ActivityEvent> = {}): ActivityEvent => ({
  id: id(number), occurredUtc: '2026-10-08T11:50:00Z', category: 'Players', action: 'CountUnavailable',
  message: 'The last player query was unavailable.', severity: 'Warning', profileId: 'server-1',
  deviceId: null, visibility: 'AssignedFriends', ...changed
})
const problem: AttentionProblemRow = {
  id: 'server-1', name: 'Evening world', warning: 'Player count is currently unavailable', priority: 50,
  category: 'Players', severity: 'Warning',
  destination: { workspace: 'host', section: 'players', profileId: 'server-1', label: 'Review players' }
}
function memoryStorage(saved?: string): AttentionStorage & { values: Map<string, string> } {
  const values = new Map<string, string>(saved === undefined ? [] : [[attentionHistoryStorageKey, saved]])
  return { values, getItem: vi.fn((key: string) => values.get(key) ?? null),
    setItem: vi.fn((key: string, value: string) => { values.set(key, value) }),
    removeItem: vi.fn((key: string) => { values.delete(key) }) }
}
const input = { mode: 'Host' as const, activitySource: 'host', activity: [event()], currentProblems: [problem], servers, nowMs }

describe('Attention current problems and history', () => {
  it('filters history by server/category/severity/time/search while time and read state do not hide current problems', () => {
    const events = [event(), event(2, { profileId: 'server-2' }), event(3, { occurredUtc: '2026-10-08T09:00:00Z' }),
      event(4, { category: 'Backup' }), event(5, { severity: 'Info' })]
    const filters = { ...defaultAttentionFilters, serverId: 'server-1', category: 'Players',
      severity: 'Warning' as const, time: 'hour' as const, search: ' EVENING ' }
    expect(filterAttentionEvents(events, filters, nowMs, servers).map(item => item.id)).toEqual([id(1)])
    expect(filterAttentionProblems([problem], { ...filters, historyState: 'dismissed' })).toEqual([problem])
    render(<AttentionWorkspace {...input} activity={[event(3, { occurredUtc: '2026-10-08T09:00:00Z' })]} storage={null} />)
    fireEvent.change(screen.getByRole('combobox', { name: 'History time' }), { target: { value: 'hour' } })
    expect(screen.getByText(problem.warning)).toBeInTheDocument()
    expect(screen.getByText('No history notices match these filters.')).toBeInTheDocument()
  })

  it('never promotes an old warning event into a current problem', () => {
    render(<AttentionWorkspace {...input} currentProblems={[]} storage={null} />)
    expect(screen.getByText('No current server problems are reported.')).toBeInTheDocument()
    expect(screen.getByText(event().message)).toBeInTheDocument()
  })

  it('counts distinct repeats after filtering and keeps the newest matching event even with unordered input', () => {
    const older = event(1, { occurredUtc: '2026-10-08T10:00:00Z', message: 'Older warning.' })
    const newest = event(2, { occurredUtc: '2026-10-08T11:59:00Z', message: 'Newest warning.' })
    const info = event(3, { severity: 'Info' })
    const groups = groupAttentionEvents([older, newest, newest, info])
    expect(groups).toHaveLength(2)
    expect(groups[0].latest).toEqual(newest)
    expect(groups[0].repeatCount).toBe(2)
    expect(groups[0].events.map(item => item.id)).toEqual([id(2), id(1)])
    const filtered = filterAttentionEvents([older, newest, info], { ...defaultAttentionFilters, time: 'hour' }, nowMs)
    expect(groupAttentionEvents(filtered).every(group => group.repeatCount === 1)).toBe(true)
  })

  it('supports individual read/dismiss and retains the active problem and optional current cards', () => {
    const storage = memoryStorage()
    const changeHistory = vi.fn()
    render(<AttentionWorkspace {...input} storage={storage} onHistoryChange={changeHistory}><p>Storage review card remains current.</p></AttentionWorkspace>)
    expect(changeHistory).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Mark read' }))
    expect(screen.getByRole('button', { name: 'Mark unread' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss notice' }))
    expect(changeHistory).toHaveBeenLastCalledWith('host', { readIds: [id(1)], dismissedIds: [id(1)] })
    expect(screen.queryByText(event().message)).not.toBeInTheDocument()
    const current = screen.getByRole('region', { name: 'Current problems' })
    expect(within(current).getByText(problem.warning)).toBeInTheDocument()
    expect(within(current).queryByRole('button', { name: /dismiss|mark read/i })).not.toBeInTheDocument()
    expect(screen.getByText('Storage review card remains current.')).toBeInTheDocument()
    const saved = JSON.parse(storage.values.get(attentionHistoryStorageKey)!)
    expect(saved).toEqual({ version: 1, sources: [{ source: 'host', readIds: [id(1)], dismissedIds: [id(1)] }] })
    expect(JSON.stringify(saved)).not.toMatch(/Evening|query|unavailable|server-1|message|profileId|deviceId/)
    fireEvent.change(screen.getByRole('combobox', { name: 'History notices' }), { target: { value: 'dismissed' } })
    expect(screen.getByText(event().message)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Restore notice' }))
    expect(readAttentionHistory(storage).sources[0].dismissedIds).toEqual([])
  })

  it('clears only retained history and lets later event IDs appear normally', () => {
    const storage = memoryStorage()
    const { rerender } = render(<AttentionWorkspace {...input} storage={storage} />)
    fireEvent.click(screen.getByRole('button', { name: 'Clear history' }))
    expect(screen.getByText(problem.warning)).toBeInTheDocument()
    expect(screen.queryByText(event().message)).not.toBeInTheDocument()
    rerender(<AttentionWorkspace {...input} activity={[event(2, { message: 'A later observation was unavailable.' }), event()]} storage={storage} />)
    expect(screen.getByText('A later observation was unavailable.')).toBeInTheDocument()
    expect(screen.queryByText(event().message)).not.toBeInTheDocument()
  })

  it('routes only reviewed categories and canonical server IDs; event text never supplies the route', () => {
    const open = vi.fn()
    render(<AttentionWorkspace {...input} currentProblems={[]} onOpen={open} activity={[
      event(1, { message: 'Open https://example.invalid/private or run a command.' }),
      event(2, { category: 'Arbitrary', profileId: null }), event(3, { category: 'Backup', profileId: 'removed-server' })
    ]} storage={null} />)
    expect(open).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Open players & timer' }))
    expect(open).toHaveBeenCalledWith({ workspace: 'host', section: 'players', profileId: 'server-1', label: 'Open players & timer' })
    expect(screen.queryByRole('button', { name: 'Open world protection' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link')).not.toBeInTheDocument()
  })

  it('hides owner-only problems/navigation in Friend mode and restricts events to assigned current-Host profiles', () => {
    const open = vi.fn()
    const openFriend = vi.fn()
    render(<AttentionWorkspace {...input} mode="Friend" activitySource={`friend:${id(100)}`} servers={[servers[0]]}
      onOpen={open} onOpenFriendServer={openFriend} storage={null} activity={[
        event(), event(2, { visibility: 'Local', message: 'Owner-only recovery.' }),
        event(3, { profileId: 'server-2', message: 'Another Host or unassigned server.' })
      ]} />)
    expect(screen.queryByText(problem.warning)).not.toBeInTheDocument()
    expect(screen.queryByText('Owner-only recovery.')).not.toBeInTheDocument()
    expect(screen.queryByText('Another Host or unassigned server.')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Open players & timer' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Open server' }))
    expect(openFriend).toHaveBeenCalledWith('server-1')
    expect(open).not.toHaveBeenCalled()
  })

  it('isolates read/dismiss IDs and filters when switching saved Host sources', () => {
    const storage = memoryStorage()
    const { rerender } = render(<AttentionWorkspace {...input} mode="Friend" activitySource={`friend:${id(100)}`} storage={storage} />)
    fireEvent.click(screen.getByRole('button', { name: 'Mark read' }))
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search attention' }), { target: { value: 'nonmatching' } })
    rerender(<AttentionWorkspace {...input} mode="Friend" activitySource={`friend:${id(101)}`} storage={storage} />)
    expect(screen.getByRole('searchbox', { name: 'Search attention' })).toHaveValue('')
    expect(screen.getByRole('button', { name: 'Mark read' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss notice' }))
    rerender(<AttentionWorkspace {...input} mode="Friend" activitySource={`friend:${id(100)}`} storage={storage} />)
    expect(screen.getByText(event().message)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Mark unread' })).toBeInTheDocument()
  })

  it('keeps announcements stable and never moves focus for a display-clock tick', () => {
    const { rerender } = render(<AttentionWorkspace {...input} storage={null} />)
    const search = screen.getByRole('searchbox', { name: 'Search attention' })
    search.focus()
    const messages = screen.getAllByRole('status').map(element => element.textContent)
    rerender(<AttentionWorkspace {...input} nowMs={nowMs + 1000} storage={null} />)
    expect(screen.getAllByRole('status').map(element => element.textContent)).toEqual(messages)
    expect(screen.getAllByRole('status').every(element => element.getAttribute('aria-live') === 'polite')).toBe(true)
    expect(search).toHaveFocus()
  })

  it('keeps read/dismiss usable for this session when storage access throws', () => {
    const storage = { getItem: () => { throw new Error('unavailable') },
      setItem: () => { throw new Error('unavailable') }, removeItem: () => { throw new Error('unavailable') } }
    render(<AttentionWorkspace {...input} storage={storage} />)
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss notice' }))
    expect(screen.queryByText(event().message)).not.toBeInTheDocument()
    expect(screen.getByText(problem.warning)).toBeInTheDocument()
  })
})

describe('bounded opaque Attention persistence', () => {
  it.each([
    'not JSON', JSON.stringify([]), JSON.stringify({ version: 2, sources: [] }),
    JSON.stringify({ version: 1, sources: [{ source: 'friend:Living room PC', readIds: [], dismissedIds: [] }] }),
    JSON.stringify({ version: 1, sources: [{ source: 'host', readIds: ['C:\\private\\save'], dismissedIds: [] }] }),
    JSON.stringify({ version: 1, sources: [{ source: 'host', readIds: [id(1), id(1)], dismissedIds: [] }] }),
    JSON.stringify({ version: 1, sources: [{ source: 'host', readIds: [], dismissedIds: [] }, { source: 'host', readIds: [], dismissedIds: [] }] }),
    JSON.stringify({ version: 1, sources: [{ source: 'host', readIds: [id(1)], dismissedIds: [], message: 'Private content' }] }),
    JSON.stringify({ version: 1, sources: [{ source: 'host', readIds: Array.from({ length: 501 }, (_, number) => id(number)), dismissedIds: [] }] })
  ])('purges malformed or nonopaque state without leaking it into the UI', saved => {
    const storage = memoryStorage(saved)
    expect(readAttentionHistory(storage)).toEqual({ version: 1, sources: [] })
    expect(storage.removeItem).toHaveBeenCalledWith(attentionHistoryStorageKey)
    expect(storage.values.has(attentionHistoryStorageKey)).toBe(false)
  })

  it('bounds retained IDs and source count and refuses names/paths as persistent source keys', () => {
    let state: AttentionHistoryState = { version: 1, sources: [] }
    const ids = Array.from({ length: 600 }, (_, number) => id(number))
    state = withAttentionHistorySource(state, 'host', { readIds: ids, dismissedIds: [...ids, 'Private message'] })
    expect(state.sources[0].readIds).toHaveLength(500)
    expect(state.sources[0].dismissedIds).toHaveLength(500)
    expect(withAttentionHistorySource(state, 'friend:PC name', { readIds: [], dismissedIds: [] })).toEqual(state)
    for (let number = 1; number <= 25; number++) state = withAttentionHistorySource(state, `friend:${id(number)}`, { readIds: [id(1)], dismissedIds: [] })
    expect(state.sources).toHaveLength(20)
    expect(state.sources.at(-1)?.source).toBe(`friend:${id(25)}`)
  })

  it('does not persist arbitrary event strings even when a typed feed contains one', () => {
    const storage = memoryStorage()
    render(<AttentionWorkspace {...input} activity={[event(1, { id: 'Private event text' })]} storage={storage} />)
    expect(screen.getByRole('button', { name: 'Mark read' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Dismiss notice' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Clear history' })).toBeDisabled()
    expect(storage.setItem).not.toHaveBeenCalled()
  })

  it('keeps a nonopaque source session-local and does not send its name to the root history callback', () => {
    const storage = memoryStorage()
    const change = vi.fn()
    render(<AttentionWorkspace {...input} activitySource="friend:Private PC name" storage={storage} onHistoryChange={change} />)
    fireEvent.click(screen.getByRole('button', { name: 'Mark read' }))
    expect(screen.getByRole('button', { name: 'Mark unread' })).toBeInTheDocument()
    expect(change).not.toHaveBeenCalled()
    expect(storage.values.get(attentionHistoryStorageKey)).not.toContain('Private PC name')
    expect(readAttentionHistory(storage).sources).toEqual([])
  })
})

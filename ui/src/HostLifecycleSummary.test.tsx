import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { Run } from './contracts'
import { HostLifecycleSummary, hostLifecycleView, type HostLifecycleSummaryProps } from './HostLifecycleSummary'

const nowMs = Date.parse('2026-10-08T12:00:00Z')
const profile: HostLifecycleSummaryProps['profile'] = { id: 'server-1', name: 'Evening world', kind: 'Valheim' }
const run: Run = {
  profileId: profile.id, state: 'Ready', detail: 'Local game query answered.', runOperationId: null, processId: 10,
  onlinePlayers: 0, maxPlayers: 10, autoShutdownAtUtc: '2026-10-08T12:10:00Z',
  autoShutdownReason: 'Waiting for the empty-server deadline.', hostAddedTime: false,
  playerNames: null, playerCountTrusted: true, friendAddedMinutes: 0, addedShutdownMinutes: 0,
  playerObservationSource: 'ValheimReady', playerCountObservedUtc: '2026-10-08T12:00:00Z'
}
const input = { profile, run, nowMs, startGate: { allowed: true } }

describe('HostLifecycleSummary', () => {
  it('keeps compact Offline details without duplicating the parent Start action', () => {
    const start = vi.fn()
    const open = vi.fn()
    render(<HostLifecycleSummary {...input} compact run={{ ...run, state: 'Offline', detail: 'Confirmed process exit.' }} onStart={start} onOpen={open} />)
    expect(screen.getByText('Confirmed process exit.')).toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
    expect(screen.queryByText('Observed phase')).not.toBeInTheDocument()
    expect(start).not.toHaveBeenCalled()
    expect(open).not.toHaveBeenCalled()
  })

  it.each(['Unknown', 'Starting'])('preserves compact %s uncertainty without self-navigation', state => {
    const start = vi.fn()
    const open = vi.fn()
    render(<HostLifecycleSummary {...input} compact run={{ ...run, state, detail: 'Waiting for fresh driver evidence.' }} onStart={start} onOpen={open} />)
    expect(screen.getByText('Waiting for fresh driver evidence.')).toBeInTheDocument()
    expect(screen.getByText(state === 'Starting' ? 'Starting · Waiting for game readiness' : 'Unknown · Review needed')).toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
    expect(start).not.toHaveBeenCalled()
    expect(open).not.toHaveBeenCalled()
  })

  it.each([
    { label: 'Review empty-server timer', section: 'players', workspace: 'host', overrides: {} },
    { label: 'Review recovery', section: 'diagnostics', workspace: 'settings', overrides: { lifecycleBlocked: true } },
    { label: 'Continue maintenance', section: 'setup', workspace: 'host', overrides: { profile: { ...profile, maintenance: { enabled: true, message: 'Updating.' } } } }
  ])('retains compact $label navigation only on owner click', ({ label, section, workspace, overrides }) => {
    const start = vi.fn()
    const open = vi.fn()
    render(<HostLifecycleSummary {...input} {...overrides} compact onStart={start} onOpen={open} />)
    expect(open).not.toHaveBeenCalled()
    expect(start).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: label }))
    expect(open).toHaveBeenCalledWith({ workspace, section, profileId: profile.id, label })
    expect(start).not.toHaveBeenCalled()
  })

  it('offers Start only on an explicit click for an Offline run and the existing parent gate', () => {
    const start = vi.fn()
    const open = vi.fn()
    const { rerender } = render(<HostLifecycleSummary {...input} run={{ ...run, state: 'Offline', runOperationId: null, processId: null }} onStart={start} onOpen={open} />)
    expect(start).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Start server' }))
    expect(start).toHaveBeenCalledOnce()
    rerender(<HostLifecycleSummary {...input} run={{ ...run, state: 'Offline', runOperationId: null, processId: null }}
      startGate={{ allowed: false, reason: 'The saved server setup has unsaved changes.' }} onStart={start} onOpen={open} />)
    expect(screen.queryByRole('button', { name: 'Start server' })).not.toBeInTheDocument()
    expect(screen.getByText(/saved server setup has unsaved changes/)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Review server setup' }))
    expect(open).toHaveBeenCalledWith({ workspace: 'host', section: 'overview', profileId: profile.id, label: 'Review server setup' })
    expect(start).toHaveBeenCalledOnce()
  })

  it.each(['Starting', 'Process running', 'Listening', 'Stopping', 'Failed', 'Unknown', 'constructor'])('does not suggest Start while canonical state is %s', state => {
    expect(hostLifecycleView({ ...input, run: { ...run, state } }).next.kind).toBe('navigate')
  })

  it.each([
    ['Process running', 'Process running · Waiting for game readiness', 'Review startup', 'recorded process is running'],
    ['Listening', 'Listening · Game readiness unverified', 'Review connection details', 'Game readiness, player count and a real game join remain unverified']
  ])('preserves the observed %s phase and offers review without inferring readiness from a cached count', (state, phase, label, reason) => {
    const start = vi.fn()
    const open = vi.fn()
    const observed = { ...run, state, detail: `${state} observed for this run.` }
    const view = hostLifecycleView({ ...input, run: observed })
    expect(view.phase).toBe(phase)
    expect(view.next).toMatchObject({ kind: 'navigate', label, destination: { workspace: 'host', section: 'overview', profileId: profile.id } })
    expect(view.next.reason).toContain(reason)
    render(<HostLifecycleSummary {...input} run={observed} busy onStart={start} onOpen={open} />)
    expect(screen.getByText(phase)).toBeInTheDocument()
    expect(screen.getByText(observed.detail)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Start server' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Review empty-server timer' })).not.toBeInTheDocument()
    const review = screen.getByRole('button', { name: label })
    expect(review).toBeEnabled()
    fireEvent.click(review)
    expect(open).toHaveBeenCalledWith({ workspace: 'host', section: 'overview', profileId: profile.id, label })
    expect(start).not.toHaveBeenCalled()
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
  })

  it('does not infer Offline from missing or another server’s run', () => {
    expect(hostLifecycleView({ ...input, run: null }).next).toMatchObject({ kind: 'navigate', label: 'Review server' })
    expect(hostLifecycleView({ ...input, run: { ...run, state: 'Offline', profileId: 'other' } }).phase).toMatch(/Unknown/)
  })

  it('keeps stale, unsupported and unknown counts on the player-evidence action', () => {
    for (const changed of [
      { ...run, playerCountObservedUtc: '2026-10-08T11:59:49Z' },
      { ...run, onlinePlayers: null, playerCountTrusted: false },
      { ...run, playerObservationSource: 'CustomScriptReady' },
      { ...run, playerCountObservedUtc: '2026-10-08T12:01:00Z' }
    ]) expect(hostLifecycleView({ ...input, run: changed }).next).toMatchObject({
      kind: 'navigate', label: 'Review player count', destination: { section: 'players' }
    })
  })

  it('uses recovery and maintenance navigation even if the parent Start gate allows Start', () => {
    const offline = { ...run, state: 'Offline', runOperationId: null, processId: null }
    expect(hostLifecycleView({ ...input, run: offline, lifecycleBlocked: true }).next).toMatchObject({
      kind: 'navigate', destination: { workspace: 'settings', section: 'diagnostics' }
    })
    expect(hostLifecycleView({ ...input, run: offline, profile: { ...profile, maintenance: { enabled: true, message: 'Updating.' } } }).next.label).toBe('Continue maintenance')
  })

  it('shows actual observed detail while Stop waits, without a progress bar or save confirmation', () => {
    render(<HostLifecycleSummary {...input} run={{ ...run, state: 'Stopping', detail: 'Waiting for the exact process to exit.' }} onOpen={vi.fn()} />)
    expect(screen.getByText('Waiting for the exact process to exit.')).toBeInTheDocument()
    expect(screen.getByText(/does not prove the world was saved/)).toBeInTheDocument()
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
  })

  it('keeps polite announcements and focus stable as the display clock advances', () => {
    const { rerender } = render(<HostLifecycleSummary {...input} onOpen={vi.fn()} />)
    const action = screen.getByRole('button', { name: 'Review empty-server timer' })
    action.focus()
    const announcement = screen.getByRole('status').textContent
    rerender(<HostLifecycleSummary {...input} nowMs={nowMs + 1000} onOpen={vi.fn()} />)
    expect(screen.getByRole('status')).toHaveTextContent(announcement!)
    expect(screen.getByRole('status')).toHaveAttribute('aria-live', 'polite')
    expect(action).toHaveFocus()
  })
})

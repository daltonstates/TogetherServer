import { fireEvent, render, screen } from '@testing-library/react'
import type { ComponentProps } from 'react'
import { describe, expect, it, vi } from 'vitest'
import type { DataRecoveryView, Run } from './contracts'
import { DataRecoveryPanel } from './DataRecoveryPanel'

const recovery: DataRecoveryView = {
  lifecycleBlocked: true,
  notices: [{ stateFile: 'C:\\private\\runs.json', quarantinedFile: 'C:\\private\\runs.invalid.json',
    reason: 'Invalid JSON', detectedUtc: '2026-09-24T12:00:00Z', blocksLifecycle: true }]
}

function run(profileId: string, state: string): Run {
  return { profileId, state, detail: `${state} detail`, processId: null, onlinePlayers: null, maxPlayers: null,
    autoShutdownAtUtc: null, autoShutdownReason: null, hostAddedTime: false, playerNames: null,
    playerCountTrusted: false, friendAddedMinutes: 0 }
}

function renderPanel(overrides: Partial<ComponentProps<typeof DataRecoveryPanel>> = {}) {
  const props: ComponentProps<typeof DataRecoveryPanel> = {
    recovery, mode: 'Host', runs: [], configuredProfileIds: [], pending: '', confirmed: false,
    onConfirmedChange: vi.fn(), onAcknowledge: vi.fn(), onSwitchToHost: vi.fn(),
    onStopRecordedRun: vi.fn(), onForgetRecordedRun: vi.fn(), ...overrides
  }
  return { ...render(<DataRecoveryPanel {...props} />), props }
}

describe('DataRecoveryPanel', () => {
  it('exposes only safe actions for orphaned recorded runs', () => {
    renderPanel({ runs: [run('live-id', 'Process running'), run('exited-id', 'Failed'), run('unknown-id', 'Unknown')] })

    expect(screen.getAllByRole('button', { name: 'Stop recorded server' })).toHaveLength(1)
    expect(screen.getAllByRole('button', { name: 'Forget exited record' })).toHaveLength(1)
    expect(screen.getByText(/Process identity is uncertain/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Acknowledge and re-enable/ })).toBeDisabled()
    expect(screen.queryByText(/C:\\private/)).not.toBeInTheDocument()
    expect(screen.getByText('runs.json')).toBeInTheDocument()
  })

  it('allows a nonblocking quarantine to be acknowledged while a server is running', () => {
    const onConfirmedChange = vi.fn()
    renderPanel({ recovery: { ...recovery, lifecycleBlocked: false }, runs: [run('live-id', 'Ready')],
      confirmed: true, onConfirmedChange })

    const checkbox = screen.getByRole('checkbox')
    expect(checkbox).not.toBeDisabled()
    expect(screen.getByRole('button', { name: 'Acknowledge warning' })).not.toBeDisabled()
    fireEvent.click(checkbox)
    expect(onConfirmedChange).toHaveBeenCalledWith(false)
  })

  it('directs Friend mode to switch before acknowledging', () => {
    const onSwitchToHost = vi.fn()
    renderPanel({ mode: 'Friend', onSwitchToHost })

    fireEvent.click(screen.getByRole('button', { name: 'Switch to My server' }))
    expect(onSwitchToHost).toHaveBeenCalledOnce()
    expect(screen.queryByRole('checkbox')).not.toBeInTheDocument()
  })
})

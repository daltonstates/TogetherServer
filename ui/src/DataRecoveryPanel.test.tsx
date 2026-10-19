import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
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
  return { profileId, state, detail: `${state} detail`, runOperationId: null, processId: null, onlinePlayers: null, maxPlayers: null,
    autoShutdownAtUtc: null, autoShutdownReason: null, hostAddedTime: false, playerNames: null,
    playerCountTrusted: false, friendAddedMinutes: 0, addedShutdownMinutes: 0,
    playerObservationSource: null, playerCountObservedUtc: null }
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
  it('dismisses and reopens recovery without acknowledging data or changing lifecycle guards', () => {
    const { props, rerender } = renderPanel({ runs: [run('unknown-id', 'Unknown')] })
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss local data warning' }))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    rerender(<DataRecoveryPanel {...props} recovery={structuredClone(props.recovery)} />)
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Review local data' }))
    expect(screen.getByRole('alert')).toBeVisible()
    expect(screen.getByRole('button', { name: /Confirm and re-enable/ })).toBeDisabled()
    expect(screen.getByRole('checkbox')).toBeDisabled()
    expect(props.onAcknowledge).not.toHaveBeenCalled()
    expect(props.onStopRecordedRun).not.toHaveBeenCalled()
    expect(props.onForgetRecordedRun).not.toHaveBeenCalled()
  })

  it('exposes only safe actions for orphaned recorded runs', () => {
    renderPanel({ runs: [run('live-id', 'Process running'), run('exited-id', 'Failed'), run('unknown-id', 'Unknown')] })

    expect(screen.getAllByRole('button', { name: 'Stop recorded server' })).toHaveLength(1)
    expect(screen.getAllByRole('button', { name: 'Forget stopped session' })).toHaveLength(1)
    expect(screen.getByText(/TogetherServer cannot identify this server process/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Confirm and re-enable/ })).toBeDisabled()
    expect(screen.queryByText(/C:\\private/)).not.toBeInTheDocument()
    expect(screen.getByText('runs.json')).toBeInTheDocument()
  })

  it('allows a nonblocking quarantine to be acknowledged while a server is running', () => {
    const onConfirmedChange = vi.fn()
    renderPanel({ recovery: { ...recovery, lifecycleBlocked: false }, runs: [run('live-id', 'Ready')],
      confirmed: true, onConfirmedChange })

    const checkbox = screen.getByRole('checkbox')
    expect(checkbox).not.toBeDisabled()
    expect(screen.getByRole('button', { name: 'Confirm review' })).not.toBeDisabled()
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

  it('asks before forgetting an exited record and preserves the process-check warning', async () => {
    const { props } = renderPanel({ runs: [run('exited-id', 'Failed')] })
    fireEvent.click(screen.getByRole('button', { name: 'Forget stopped session' }))
    const dialog = await screen.findByRole('dialog', { name: 'Forget stopped session' })
    expect(within(dialog).getByText('Forget this saved session only if TogetherServer confirms that its server process is no longer running?')).toBeInTheDocument()
    expect(props.onForgetRecordedRun).not.toHaveBeenCalled()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Forget stopped session' }))
    await waitFor(() => expect(props.onForgetRecordedRun).toHaveBeenCalledWith('exited-id'))
    expect(props.onAcknowledge).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: /Confirm and re-enable/ })).toBeDisabled()
  })

  it('cancels forgetting without invoking the owner callback', async () => {
    const { props } = renderPanel({ runs: [run('exited-id', 'Failed')] })
    fireEvent.click(screen.getByRole('button', { name: 'Forget stopped session' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(props.onForgetRecordedRun).not.toHaveBeenCalled()
  })

  it.each([
    { runs: [run('exited-id', 'Unknown')] },
    { runs: [run('exited-id', 'Ready')] },
    { runs: [run('other-id', 'Failed')] },
    { runs: [{ ...run('exited-id', 'Failed'), processId: 123 }] },
    { configuredProfileIds: ['exited-id'] },
    { pending: 'other-action' },
    { mode: 'Friend' as const },
    { recovery: { lifecycleBlocked: false, notices: [] } }
  ])('does not forget when the current record or guards change during confirmation: %j', async changed => {
    const { props, rerender } = renderPanel({ runs: [run('exited-id', 'Failed')] })
    fireEvent.click(screen.getByRole('button', { name: 'Forget stopped session' }))
    const dialog = await screen.findByRole('dialog')
    rerender(<DataRecoveryPanel {...props} {...changed} />)
    expect(screen.getByRole('dialog')).toBe(dialog)
    fireEvent.click(within(dialog).getByRole('button', { name: 'Forget stopped session' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(props.onForgetRecordedRun).not.toHaveBeenCalled()
    expect(props.onAcknowledge).not.toHaveBeenCalled()
  })
})

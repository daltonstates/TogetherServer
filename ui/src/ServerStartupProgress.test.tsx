import { fireEvent, render, screen, within } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { ServerStartupProgress } from './ServerStartupProgress'

describe('server startup progress', () => {
  it('keeps launch indeterminate, then shows the game check until canonical readiness arrives', () => {
    const view = render(<ServerStartupProgress state="Offline" requestPending detail="No managed process" />)
    const launch = screen.getByRole('progressbar', { name: 'Checking setup and starting' })
    expect(launch).not.toHaveAttribute('aria-valuenow')
    expect(launch).toHaveAccessibleDescription(/Checking the saved setup and available ports/)
    expect(screen.queryByText('No managed process')).not.toBeInTheDocument()
    expect(screen.getByText('Launch server').closest('li')).toHaveAttribute('aria-current', 'step')

    view.rerender(<ServerStartupProgress state="Unknown" playerObservationSource="ObservationPending"
      runOperationId="new-run" detail="Waiting for the next server check." />)
    expect(screen.getByRole('progressbar', { name: 'Waiting for the game' })).not.toHaveAttribute('aria-valuenow')
    expect(screen.getByText('Check game').closest('li')).toHaveAttribute('aria-current', 'step')
    expect(screen.getByText('Ready').closest('li')).not.toHaveAttribute('aria-current')
    expect(screen.getByRole('status')).toHaveTextContent('Waiting for the next server check.')

    view.rerender(<ServerStartupProgress state="Starting" detail="Waiting for a local game status reply." />)
    expect(screen.getByRole('progressbar')).toHaveAccessibleDescription('Waiting for a local game status reply.')
    view.rerender(<ServerStartupProgress state="Ready" />)
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
  })

  it.each(['Ready', 'Listening', 'Stopping', 'Failed', 'Unknown', 'World copy in progress'])(
    'does not let an unfinished Start request obscure %s', state => {
      render(<ServerStartupProgress state={state} requestPending />)
      expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
    })

  it.each(['ObservationStale', 'HealthProbeFailed', null])('does not relabel %s uncertainty as startup', source => {
    render(<ServerStartupProgress state="Unknown" playerObservationSource={source} runOperationId="existing-run" />)
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
  })

  it('requires an identified run before treating the first pending observation as launched', () => {
    render(<ServerStartupProgress state="Unknown" playerObservationSource="ObservationPending" />)
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
  })

  it('opens logs only on an explicit click without moving focus when details update', () => {
    const openLogs = vi.fn()
    const view = render(<ServerStartupProgress state="Starting" detail="Checking the game." onOpenLogs={openLogs} />)
    const logs = screen.getByRole('button', { name: 'View startup logs' })
    logs.focus()
    view.rerender(<ServerStartupProgress state="Starting" detail="Still waiting for a game reply." onOpenLogs={openLogs} />)
    expect(logs).toHaveFocus()
    expect(openLogs).not.toHaveBeenCalled()
    expect(within(screen.getByRole('region', { name: 'Server startup' })).getByRole('status')).toHaveTextContent('Still waiting for a game reply.')
    fireEvent.click(logs)
    expect(openLogs).toHaveBeenCalledOnce()
  })
})

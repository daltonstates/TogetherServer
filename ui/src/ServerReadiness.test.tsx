import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { ServerReadiness, type PortDiagnostics } from './ServerReadiness'
import type { Run } from './contracts'

function diagnostics(state: string, detail: string): PortDiagnostics {
  return {
    checkedUtc: '2026-09-26T20:00:00Z',
    games: [{ profileId: 'server-1', label: 'Server', ports: [2456, 2457], protocol: 'UDP',
      state: 'Waiting', detail: 'Start the server to check its game ports.', routeKind: 'Direct', kind: 'Valheim' }],
    control: {
      port: 5131, state, detail, remoteState: state === 'Idle' ? 'Not needed' : 'Not verified',
      remoteDetail: state === 'Idle'
        ? 'No active server code or connected PC needs a Friend connection.'
        : 'No Friend PC has contacted this Host recently.',
      bindAddress: '0.0.0.0', bindScope: 'All IPv4 interfaces', endpoint: 'https://1.2.3.4:5131',
      endpointState: 'Address hint', endpointDetail: 'The saved address matches the latest lookup.',
      lanAddresses: [], lanForwardDetail: 'No LAN address is available.'
    }
  }
}

describe('ServerReadiness Friend connection wording', () => {
  it('keeps count-only observations neutral while exposing actual identity failures', () => {
    const pending: Run = { profileId: 'server-1', runOperationId: null, state: 'Unknown', detail: 'Waiting for a fresh probe.',
      processId: 123, onlinePlayers: null, maxPlayers: null, autoShutdownAtUtc: null, autoShutdownReason: null,
      hostAddedTime: false, playerNames: null, playerCountTrusted: false, friendAddedMinutes: 0, addedShutdownMinutes: 0,
      playerObservationSource: 'ObservationStale', playerCountObservedUtc: null, playerCountRefreshing: true, playerCountFailureCount: 0 }
    const props = { profileId: 'server-1', status: 'Unknown', ports: diagnostics('Idle', 'Idle'), routeCheck: null,
      onRefresh: vi.fn(), busy: false }
    const view = render(<ServerReadiness {...props} run={pending} />)
    expect(screen.getByText('Server status is currently unavailable.',
      { selector: '.readiness-summary-detail' })).toBeVisible()
    expect(screen.queryByText('The server state is unknown.')).not.toBeInTheDocument()
    view.rerender(<ServerReadiness {...props} run={{ ...pending, playerCountRefreshing: false, playerCountFailureCount: 2 }} />)
    expect(screen.queryByText('The server state is unknown.')).not.toBeInTheDocument()
    view.rerender(<ServerReadiness {...props} run={{ ...pending, playerCountRefreshing: false, playerObservationSource: null }} />)
    expect(screen.getByText('The server state is unknown.')).toBeVisible()
    const closed = diagnostics('Idle', 'Idle')
    closed.games[0] = { ...closed.games[0], state: 'Closed on PC', detail: 'The game port is closed.' }
    view.rerender(<ServerReadiness {...props} ports={closed} run={{ ...pending, playerCountFailureCount: 2 }} />)
    expect(screen.getByText('The game connection needs attention.')).toBeVisible()
    expect(screen.getAllByText('The game port is closed.').some(element => element.closest('.readiness-action'))).toBe(true)
  })

  it('presents an unused listener as a normal idle state without a repair warning', () => {
    render(<ServerReadiness profileId="server-1" status="Offline"
      ports={diagnostics('Idle', 'Friend access is idle. Nothing is wrong.')}
      routeCheck={null} onRefresh={vi.fn()} onOpenConnection={vi.fn()} busy={false} />)

    expect(screen.getAllByText('Idle')).not.toHaveLength(0)
    expect(screen.getByText('Nothing needs fixing. Choose Invite friends when another PC needs the server code.')).toBeInTheDocument()
    expect(screen.getByText('Not needed while idle')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Fix connection' })).not.toBeInTheDocument()
    expect(screen.queryByText('Friend connections need attention.')).not.toBeInTheDocument()
  })

  it('keeps a real listener failure actionable', () => {
    const onOpenConnection = vi.fn()
    render(<ServerReadiness profileId="server-1" status="Offline"
      ports={diagnostics('Not listening', 'Friend access could not start because its TCP port is already in use.')}
      routeCheck={null} onRefresh={vi.fn()} onOpenConnection={onOpenConnection} busy={false} />)

    expect(screen.getByText('Friend connections need attention.')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Fix connection' }))
    expect(onOpenConnection).toHaveBeenCalledOnce()
  })
})

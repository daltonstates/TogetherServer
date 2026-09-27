import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { ServerReadiness, type PortDiagnostics } from './ServerReadiness'

function diagnostics(state: string, detail: string): PortDiagnostics {
  return {
    checkedUtc: '2026-09-26T20:00:00Z',
    games: [{ profileId: 'server-1', label: 'Server', ports: [2456, 2457], protocol: 'UDP',
      state: 'Waiting', detail: 'Start the server to check its game ports.', routeKind: 'Direct', kind: 'Valheim' }],
    control: {
      port: 5131, state, detail, remoteState: state === 'Idle' ? 'Not needed' : 'Not verified',
      remoteDetail: state === 'Idle'
        ? 'No active invite or usable paired PC needs the Friend listener.'
        : 'No authenticated Friend heartbeat is current.',
      bindAddress: '0.0.0.0', bindScope: 'All IPv4 interfaces', endpoint: 'https://1.2.3.4:5131',
      endpointState: 'Address hint', endpointDetail: 'The saved address matches the latest lookup.',
      lanAddresses: [], lanForwardDetail: 'No LAN address is available.'
    }
  }
}

describe('ServerReadiness Friend listener wording', () => {
  it('presents an unused listener as a normal idle state without a repair warning', () => {
    render(<ServerReadiness profileId="server-1" status="Offline"
      ports={diagnostics('Idle', 'Friend access is idle. Nothing is wrong.')}
      routeCheck={null} onRefresh={vi.fn()} onOpenConnection={vi.fn()} busy={false} />)

    expect(screen.getAllByText('Idle')).not.toHaveLength(0)
    expect(screen.getByText('Nothing needs fixing. Create an invite when another PC needs to pair.')).toBeInTheDocument()
    expect(screen.getByText('Not needed while idle')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Fix connection' })).not.toBeInTheDocument()
    expect(screen.queryByText('The Friend listener needs attention.')).not.toBeInTheDocument()
  })

  it('keeps a real listener failure actionable', () => {
    const onOpenConnection = vi.fn()
    render(<ServerReadiness profileId="server-1" status="Offline"
      ports={diagnostics('Not listening', 'Friend access could not start because its TCP port is already in use.')}
      routeCheck={null} onRefresh={vi.fn()} onOpenConnection={onOpenConnection} busy={false} />)

    expect(screen.getByText('The Friend listener needs attention.')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Fix connection' }))
    expect(onOpenConnection).toHaveBeenCalledOnce()
  })
})

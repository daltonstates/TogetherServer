import { render, screen } from '@testing-library/react'
import { expect, it, vi } from 'vitest'
import type { Profile } from './GameProfile'
import { HostStartConnectionNotice } from './StartConnectionNotice'
import type { PortDiagnostics } from './ServerReadiness'

const profile: Profile = {
  id: 'server-1', kind: 'Valheim', name: 'World', serverName: 'World', crossplay: false,
  publicListing: false, worldId: 'world', worldSource: 'New', worldDirectory: '',
  gamePort: 2456, executablePath: ''
}

const ports: PortDiagnostics = {
  checkedUtc: new Date().toISOString(),
  games: [{ profileId: 'server-1', label: 'World', ports: [2456, 2457], protocol: 'UDP',
    state: 'Waiting', detail: 'Start the game first.', routeKind: 'Direct' }],
  control: { port: 5131, state: 'Open on PC', detail: 'Listening locally.',
    remoteState: 'Not verified', remoteDetail: 'No Friend heartbeat.', bindScope: 'All IPv4 interfaces',
    endpoint: 'https://1.2.3.4:5131', endpointState: 'Address hint' }
}

it('keeps a passed outside control TCP check separate from the game route', () => {
  vi.useFakeTimers()
  vi.setSystemTime(new Date('2026-10-04T12:00:00Z'))
  render(<HostStartConnectionNotice profile={profile} ports={ports}
    routeCheck={{ state: 'Reachable', detail: 'TCP reached.', port: 5131,
      endpoint: 'https://1.2.3.4:5131', checkedUtc: '2026-10-04T11:59:00Z' }} />)
  expect(screen.getByText(/UDP 2456, 2457/)).toBeInTheDocument()
  expect(screen.getByText(/Incoming game access can only be tested after the server is listening/)).toBeInTheDocument()
  expect(screen.getByText(/An outside TCP check reached Friend app port 5131/)).toBeInTheDocument()
  expect(screen.getByText(/Pairing and the game route still need separate checks/)).toBeInTheDocument()
  vi.useRealTimers()
})

it('shows every declared custom port and highlights a failed outside control check', () => {
  vi.useFakeTimers()
  vi.setSystemTime(new Date('2026-10-04T12:00:00Z'))
  const custom: Profile = { ...profile, kind: 'Custom', gamePort: 8211,
    custom: { gameName: 'Custom', primaryProtocol: 'UDP', shareJoinAddress: true,
      additionalPorts: [{ protocol: 'TCP', port: 27015, label: 'Query', family: 'Any' }] } }
  const { container } = render(<HostStartConnectionNotice profile={custom} ports={ports}
    routeCheck={{ state: 'Not reachable', detail: 'TCP blocked.', port: 5131,
      endpoint: 'https://1.2.3.4:5131', checkedUtc: '2026-10-04T11:59:00Z' }} />)
  expect(screen.getByText(/UDP 8211, TCP 27015 \(Query\)/)).toBeInTheDocument()
  expect(screen.getByText(/could not reach Friend app port 5131/)).toBeInTheDocument()
  expect(container.querySelector('.start-connection-notice.attention')).toBeInTheDocument()
  vi.useRealTimers()
})

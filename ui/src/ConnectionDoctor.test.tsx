import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { ConnectionDoctor, connectionDoctorStages } from './ConnectionDoctor'
import type { Device, Run } from './contracts'
import type { Profile } from './GameProfile'
import type { PortDiagnostics } from './ServerReadiness'

const profile = { id: 'server', kind: 'Valheim', name: 'Weekend', serverName: 'Weekend', worldId: 'world' } as Profile
const run = { profileId: 'server', state: 'Ready', detail: 'Ready', processId: 1, onlinePlayers: 0,
  maxPlayers: 10, autoShutdownAtUtc: null, autoShutdownReason: null, hostAddedTime: false,
  playerNames: null, playerCountTrusted: true, friendAddedMinutes: 0, addedShutdownMinutes: 0,
  playerObservationSource: 'ValheimQuery', playerCountObservedUtc: '2026-09-29T12:00:00Z' } satisfies Run
const ports: PortDiagnostics = {
  checkedUtc: '2026-09-29T12:00:00Z', games: [{ profileId: 'server', label: 'Valheim', ports: [2456, 2457],
    protocol: 'UDP', state: 'Open on PC', detail: 'Game ports are open locally.', routeKind: 'Direct', kind: 'Valheim' }],
  control: { port: 5131, state: 'Open on PC', detail: 'Listener is open.', remoteState: 'Waiting', remoteDetail: '',
    bindScope: 'Network', endpoint: 'https://203.0.113.10:5131', endpointState: 'Address hint', endpointDetail: 'Address is current.' }
}
const device: Device = { id: 'device', profileId: 'server', assignedProfileIds: ['server'], name: 'Alex PC',
  canStart: true, canStop: false, canExtendTimer: false, canViewLogs: false, revoked: false, paired: true,
  approvalPending: false, credentialExpiresUtc: null, lastHeartbeatUtc: '2026-09-29T11:59:50Z', serverPermissions: [],
  accessExpiresUtc: null, accessExpired: false }

describe('ConnectionDoctor', () => {
  it('keeps TCP, authentication, and a real join as separate evidence stages', () => {
    const stages = connectionDoctorStages(profile, run, ports, {
      state: 'Reachable', detail: 'Reached.', port: 5131, checkedUtc: '2026-09-29T11:59:55Z', endpoint: 'https://203.0.113.10:5131'
    }, [device], Date.parse('2026-09-29T12:00:00Z'))

    expect(stages.find(stage => stage.id === 'route')).toMatchObject({ state: 'Reached this PC', complete: true })
    expect(stages.find(stage => stage.id === 'friend')).toMatchObject({ state: 'Authenticated recently', complete: true })
    expect(stages.find(stage => stage.id === 'join')).toMatchObject({ state: 'Not proven', complete: false })
  })

  it('offers fixed local actions and never a router mutation', () => {
    const onRefresh = vi.fn()
    const onTestRoute = vi.fn()
    render(<ConnectionDoctor profile={profile} run={run} ports={ports} routeCheck={null} devices={[]}
      busy={false} routeMode="DirectInternet" onRefresh={onRefresh} onTestRoute={onTestRoute}
      onOpenAccess={vi.fn()} onOpenDiagnostics={vi.fn()} />)

    fireEvent.click(screen.getByRole('button', { name: 'Run checks' }))
    fireEvent.click(screen.getByRole('button', { name: 'Test outside TCP route' }))
    fireEvent.click(screen.getByRole('button', { name: 'Test with a Friend' }))
    expect(onRefresh).toHaveBeenCalledOnce()
    expect(onTestRoute).toHaveBeenCalledOnce()
    expect(screen.getByText('Live coordinated check')).toBeInTheDocument()
    expect(screen.getByText(/does not change firewall, router, DNS/)).toBeInTheDocument()
  })
})

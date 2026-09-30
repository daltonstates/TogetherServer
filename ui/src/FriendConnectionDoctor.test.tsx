import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { FriendConnectionDoctor, friendConnectionDoctorStages } from './FriendConnectionDoctor'
import type { FriendSnapshot } from './contracts'

const snapshot: FriendSnapshot = {
  mode: 'Friend', state: 'Connected', detail: 'Connected to https://192.0.2.44:5131.',
  endpoint: 'https://192.0.2.44:5131', lastConnectedUtc: '2026-09-30T12:00:00Z',
  remoteControlsEnabled: true, canStart: true, canStop: true, connectionId: 'connection-secret',
  connections: null, connectionCode: 'internal-issue-marker', hostVersion: '0.2.1', friendVersion: '0.2.1',
  hostProtocolVersion: 6, hostCapabilities: [], protocolCompatible: true,
  profiles: [{ id: 'private-profile-id', name: 'Private server name', state: 'Ready',
    joinAddress: '198.51.100.8:2456', canStopNow: true, stopReason: null, kind: 'Valheim',
    onlinePlayers: 0, maxPlayers: 10, autoShutdownAtUtc: null, autoShutdownReason: null,
    canStart: false, canStop: true, canRestartNow: true, restartReason: null,
    maintenanceEnabled: false, maintenanceMessage: null, canExtendTimer: true,
    timerExtensionMinutes: 15, timerExtensionRemainingMinutes: 60, canViewLogs: false }]
}

describe('FriendConnectionDoctor', () => {
  it('keeps authenticated app access and real game reachability as separate stages', () => {
    const stages = friendConnectionDoctorStages(snapshot, {})
    expect(stages.find(stage => stage.id === 'route')).toMatchObject({ state: 'Answered', complete: true })
    expect(stages.find(stage => stage.id === 'identity')).toMatchObject({ state: 'Matched', complete: true })
    expect(stages.find(stage => stage.id === 'credential')).toMatchObject({ state: 'Authenticated', complete: true })
    expect(stages.find(stage => stage.id === 'game')).toMatchObject({ state: 'Not tested', complete: false })
  })

  it('runs only existing fixed actions and copies a report without endpoint or profile details', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
    const onRefresh = vi.fn()
    const onProbe = vi.fn()
    render(<FriendConnectionDoctor snapshot={snapshot} gameResults={{}} busy={false}
      onRefresh={onRefresh} onProbe={onProbe} />)

    fireEvent.click(screen.getByRole('button', { name: 'Run checks' }))
    fireEvent.click(screen.getByRole('button', { name: 'Check game endpoint' }))
    fireEvent.click(screen.getByRole('button', { name: 'Copy redacted report' }))
    expect(onRefresh).toHaveBeenCalledOnce()
    expect(onProbe).toHaveBeenCalledWith('private-profile-id')
    await waitFor(() => expect(writeText).toHaveBeenCalledOnce())
    const report = String(writeText.mock.calls[0][0])
    expect(report).not.toContain('192.0.2.44')
    expect(report).not.toContain('198.51.100.8')
    expect(report).not.toContain('Private server name')
    expect(report).not.toContain('private-profile-id')
    expect(report).not.toContain('connection-secret')
    expect(report).not.toContain('internal-issue-marker')
  })
})

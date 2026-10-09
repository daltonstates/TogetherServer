import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { FriendConnectionDoctor, friendConnectionDoctorStages, friendCheckFreshness,
  friendGameCheckIdentity, friendGameProbeSupported, friendDoctorReport, friendDoctorTroubleshooting, FriendGameCheckResult } from './FriendConnectionDoctor'
import type { FriendSnapshot, GameEndpointResult } from './contracts'

const nowMs = Date.parse('2026-09-30T12:00:10Z')

const snapshot: FriendSnapshot = {
  mode: 'Friend', state: 'Connected', detail: 'Connected to https://192.0.2.44:5131.',
  endpoint: 'https://192.0.2.44:5131', lastConnectedUtc: '2026-09-30T12:00:00Z',
  remoteControlsEnabled: true, canStart: true, canStop: true, connectionId: 'connection-secret',
  connections: null, connectionCode: 'internal-issue-marker', hostVersion: '0.2.1', friendVersion: '0.2.1',
  hostProtocolVersion: 6, hostCapabilities: [], protocolCompatible: true,
  profiles: [{ id: 'private-profile-id', name: 'Private server name', state: 'Ready', runOperationId: '22222222-2222-4222-8222-222222222222',
    joinAddress: '198.51.100.8:2456', canStopNow: true, stopReason: null, kind: 'Valheim',
    onlinePlayers: 0, maxPlayers: 10, autoShutdownAtUtc: null, autoShutdownReason: null,
    canStart: false, canStop: true, canRestartNow: true, restartReason: null,
    maintenanceEnabled: false, maintenanceMessage: null, canExtendTimer: true,
    timerExtensionMinutes: 15, timerExtensionRemainingMinutes: 60, canViewLogs: false }]
}

describe('FriendConnectionDoctor', () => {
  it('keeps authenticated app access and real game reachability as separate stages', () => {
    const stages = friendConnectionDoctorStages(snapshot, {}, { nowMs })
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
    render(<FriendConnectionDoctor snapshot={snapshot} gameResults={{}} busy={false} nowMs={nowMs}
      onRefresh={onRefresh} onProbe={onProbe} />)

    fireEvent.click(screen.getByText(/^Connection Doctor/))
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

  it('collapses healthy stages and expands the failed stage without granting later evidence', () => {
    const view = render(<FriendConnectionDoctor snapshot={snapshot} gameResults={{}} busy={false} nowMs={nowMs}
      onRefresh={vi.fn()} onProbe={vi.fn()} />)
    expect(view.container.querySelector('.friend-connection-doctor')).not.toHaveAttribute('open')
    expect(view.container.querySelectorAll('.doctor-stage details[open]')).toHaveLength(0)
    view.rerender(<FriendConnectionDoctor snapshot={{ ...snapshot, state: 'Disconnected/Unknown', connectionCode: 'HostIdentityMismatch' }}
      gameResults={{}} busy={false} nowMs={nowMs} onRefresh={vi.fn()} onProbe={vi.fn()} />)
    expect(view.container.querySelector('.friend-connection-doctor')).toHaveAttribute('open')
    expect(view.container.querySelector('.doctor-stage.error details')).toHaveAttribute('open')
    expect(screen.getByText(/Next: Pinned Host identity/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Check game endpoint' })).toBeDisabled()
  })

  it('requires a server choice for several assignments and probes only that choice', () => {
    const other = { ...snapshot.profiles[0], id: 'second', name: 'Second server' }
    const onProbe = vi.fn()
    render(<FriendConnectionDoctor snapshot={{ ...snapshot, profiles: [...snapshot.profiles, other] }}
      gameResults={{}} busy={false} nowMs={nowMs} onRefresh={vi.fn()} onProbe={onProbe} />)
    fireEvent.click(screen.getByText(/^Connection Doctor/))
    expect(screen.getByRole('button', { name: 'Check game endpoint' })).toBeDisabled()
    fireEvent.change(screen.getByLabelText('Server to check'), { target: { value: 'second' } })
    fireEvent.click(screen.getByRole('button', { name: 'Check game endpoint' }))
    expect(onProbe).toHaveBeenCalledExactlyOnceWith('second')
  })

  it('does not let another server reply pass the selected server', () => {
    const other = { ...snapshot.profiles[0], id: 'second', name: 'Second server' }
    const states = friendConnectionDoctorStages({ ...snapshot, profiles: [...snapshot.profiles, other] }, { second: answered },
      { nowMs, selectedProfileId: snapshot.profiles[0].id })
    expect(states.find(stage => stage.id === 'game')).toMatchObject({ state: 'Not tested', complete: false })
    expect(friendConnectionDoctorStages({ ...snapshot, profiles: [...snapshot.profiles, other] }, { second: answered }, { nowMs })
      .find(stage => stage.id === 'game')).toMatchObject({ state: 'Choose a server', complete: false })
  })

  it('drops a removed selection and clears a selection when the Host changes', () => {
    const other = { ...snapshot.profiles[0], id: 'second', name: 'Second server' }
    const view = render(<FriendConnectionDoctor snapshot={{ ...snapshot, profiles: [...snapshot.profiles, other] }}
      gameResults={{}} busy={false} nowMs={nowMs} onRefresh={vi.fn()} onProbe={vi.fn()} />)
    fireEvent.click(screen.getByText(/^Connection Doctor/))
    fireEvent.change(screen.getByLabelText('Server to check'), { target: { value: other.id } })
    view.rerender(<FriendConnectionDoctor snapshot={snapshot} gameResults={{ second: answered }} busy={false}
      nowMs={nowMs} onRefresh={vi.fn()} onProbe={vi.fn()} />)
    expect(screen.getByRole('button', { name: 'Check game endpoint' })).toBeDisabled()
    view.rerender(<FriendConnectionDoctor snapshot={{ ...snapshot, connectionId: 'another', profiles: [...snapshot.profiles, other] }}
      gameResults={{}} busy={false} nowMs={nowMs} onRefresh={vi.fn()} onProbe={vi.fn()} />)
    fireEvent.click(screen.getByText(/^Connection Doctor/))
    expect(screen.getByLabelText('Server to check')).toHaveValue('')
  })

  it('keeps stale heartbeat and game results incomplete, with their original times', () => {
    const states = friendConnectionDoctorStages(snapshot, { [snapshot.profiles[0].id]: answered }, { nowMs: nowMs + 46_000 })
    expect(states.find(stage => stage.id === 'route')).toMatchObject({ state: 'Stale', complete: false, checkedUtc: '2026-09-30T12:00:00.000Z' })
    expect(states.find(stage => stage.id === 'game')).toMatchObject({ state: 'Waiting', complete: false, freshness: 'stale' })
    const freshApp = { ...snapshot, lastConnectedUtc: new Date(nowMs + 46_000).toISOString() }
    expect(friendConnectionDoctorStages(freshApp, { [snapshot.profiles[0].id]: answered }, { nowMs: nowMs + 46_000 })
      .find(stage => stage.id === 'game')).toMatchObject({ state: 'Stale', complete: false })
  })

  it.each([null, 'not-a-time', '2026-10-01T12:00:00Z'])('refuses unverified check time %s', checkedUtc => {
    expect(friendCheckFreshness(checkedUtc, nowMs)).toBe('unknown')
    expect(friendConnectionDoctorStages({ ...snapshot, lastConnectedUtc: checkedUtc }, {}, { nowMs })
      .find(stage => stage.id === 'identity')?.complete).toBe(false)
  })

  it('binds a reply to its Host, route, selected server and invalidation revision', () => {
    const profile = snapshot.profiles[0]
    const identities = { [profile.id]: friendGameCheckIdentity(snapshot, profile, 1) }
    expect(friendConnectionDoctorStages(snapshot, { [profile.id]: answered }, { nowMs, identityKey: 1, gameResultIdentities: identities })
      .find(stage => stage.id === 'game')?.complete).toBe(true)
    for (const changed of [{ ...snapshot, endpoint: 'https://192.0.2.5:5131' }, { ...snapshot, connectionId: 'other' },
      { ...snapshot, profiles: [{ ...profile, runOperationId: '22222222-2222-4222-8222-222222222222', joinAddress: '192.0.2.6:2456' }] }]) {
      expect(friendConnectionDoctorStages(changed, { [profile.id]: answered }, { nowMs, identityKey: 1, gameResultIdentities: identities })
        .find(stage => stage.id === 'game')).toMatchObject({ state: 'Changed', complete: false })
    }
    expect(friendConnectionDoctorStages(snapshot, { [profile.id]: answered }, { nowMs, identityKey: 2, gameResultIdentities: identities })
      .find(stage => stage.id === 'game')).toMatchObject({ state: 'Changed', complete: false })
  })

  it('supports Terraria TCP only and never upgrades it to readiness or occupancy', () => {
    const profile = { ...snapshot.profiles[0], kind: 'Terraria', state: 'Listening', onlinePlayers: null }
    const states = friendConnectionDoctorStages({ ...snapshot, profiles: [profile] },
      { [profile.id]: { ...answered, code: 'GamePortOpen' } }, { nowMs })
    expect(friendGameProbeSupported(profile)).toBe(true)
    expect(states.find(stage => stage.id === 'game')).toMatchObject({ state: 'TCP listener reached', complete: true })
    expect(states.find(stage => stage.id === 'readiness')).toMatchObject({ state: 'Listening only', complete: false })
    expect(friendConnectionDoctorStages({ ...snapshot, profiles: [profile] }, { [profile.id]: answered }, { nowMs })
      .find(stage => stage.id === 'game')?.complete).toBe(false)
  })

  it('invalidates a recent reply after the canonical run changes at the same game address', () => {
    const profile = snapshot.profiles[0]
    const identities = { [profile.id]: friendGameCheckIdentity(snapshot, profile) }
    const restarted = { ...profile, runOperationId: '33333333-3333-4333-8333-333333333333' }
    const game = friendConnectionDoctorStages({ ...snapshot, profiles: [restarted] }, { [profile.id]: answered },
      { nowMs, gameResultIdentities: identities }).find(stage => stage.id === 'game')
    expect(game).toMatchObject({ state: 'Changed', complete: false })
  })

  it.each(['Factorio', 'Custom', 'Fixture'])('does not invent a probe for %s', kind => {
    const profile = { ...snapshot.profiles[0], kind }
    expect(friendGameProbeSupported(profile)).toBe(false)
    expect(friendConnectionDoctorStages({ ...snapshot, profiles: [profile] }, { [profile.id]: answered }, { nowMs })
      .find(stage => stage.id === 'game')).toMatchObject({ state: 'Manual check', complete: false })
  })

  it.each(['game.example:2456', '[2001:db8::1]:2456'])('does not offer an IPv4-only probe for %s', joinAddress => {
    expect(friendGameProbeSupported({ ...snapshot.profiles[0], joinAddress })).toBe(false)
  })

  it('does not probe beyond the valid Valheim query-port range', () => {
    expect(friendGameProbeSupported({ ...snapshot.profiles[0], joinAddress: '192.0.2.1:65535' })).toBe(false)
  })

  it('keeps controls paused separate from authenticated access and offline readiness', () => {
    const states = friendConnectionDoctorStages({ ...snapshot, state: 'Disabled', remoteControlsEnabled: false,
      profiles: [{ ...snapshot.profiles[0], state: 'Offline' }] }, { [snapshot.profiles[0].id]: answered }, { nowMs })
    expect(states.find(stage => stage.id === 'credential')?.complete).toBe(true)
    expect(states.find(stage => stage.id === 'controls')?.state).toBe('Paused by Host')
    expect(states.find(stage => stage.id === 'readiness')?.state).toBe('Offline')
    expect(states.find(stage => stage.id === 'game')?.complete).toBe(false)
  })

  it('does not credit an unknown app protocol or a newer denial beside old successful cards', () => {
    expect(friendConnectionDoctorStages({ ...snapshot, protocolCompatible: undefined }, {}, { nowMs })
      .find(stage => stage.id === 'protocol')?.complete).toBe(false)
    expect(friendConnectionDoctorStages({ ...snapshot, connectionCode: 'AccessExpired' }, { [snapshot.profiles[0].id]: answered }, { nowMs })
      .find(stage => stage.id === 'credential')).toMatchObject({ state: 'Owner access ended', complete: false })
  })

  it('copies only fixed concise explanations, never remote error or game message content', () => {
    const states = friendConnectionDoctorStages({ ...snapshot, state: 'Disconnected/Unknown', connectionCode: 'HostIdentityMismatch',
      detail: 'SecretPassword https://192.0.2.44:5131' }, { [snapshot.profiles[0].id]: { ...answered, message: 'Private Player TS3:secret' } }, { nowMs })
    const explanation = friendDoctorTroubleshooting(states)
    expect(explanation).toContain('Pinned Host identity: Mismatch')
    const report = friendDoctorReport(states, nowMs)
    for (const secret of ['SecretPassword', '192.0.2.44', 'Private Player', 'TS3:secret', snapshot.connectionId, snapshot.profiles[0].id])
      expect(report).not.toContain(secret)
  })

  it('labels a card’s old reply as stale and omits the raw endpoint message', () => {
    const profile = snapshot.profiles[0]
    const later = nowMs + 46_000
    const view = render(<FriendGameCheckResult snapshot={{ ...snapshot, lastConnectedUtc: new Date(later).toISOString() }}
      profile={profile} gameResults={{ [profile.id]: { ...answered, message: 'Private endpoint 192.0.2.44' } }} nowMs={later} />)
    expect(screen.getByRole('status')).toHaveTextContent('Game endpoint: Stale')
    expect(view.container.querySelector('time')).toHaveAttribute('dateTime', '2026-09-30T12:00:00.000Z')
    expect(view.container.textContent).not.toContain('192.0.2.44')
  })
})

const answered: GameEndpointResult = { answered: true, code: 'GameEndpointAnswered', message: 'A query answered.',
  checkedUtc: '2026-09-30T12:00:00Z', onlinePlayers: 0, maxPlayers: 10 }

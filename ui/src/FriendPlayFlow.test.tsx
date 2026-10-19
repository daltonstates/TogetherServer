import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import type { FriendSnapshot, PublicProfile } from './contracts'
import { effectiveFriendPermissions, friendConnectionExplanation, FriendConnectionExplanation, FriendPlayFlow } from './FriendPlayFlow'

const nowMs = Date.parse('2026-10-08T15:00:10Z')
const profile: PublicProfile = {
  id: '11111111-1111-4111-8111-111111111111', name: 'Synthetic server', kind: 'Valheim', state: 'Ready',
  runOperationId: '22222222-2222-4222-8222-222222222222', joinAddress: '192.0.2.10:2456', canStart: true, canStop: true, canStopNow: true, stopReason: null,
  canRestartNow: true, restartReason: null, onlinePlayers: 0, maxPlayers: 10,
  autoShutdownAtUtc: null, autoShutdownReason: null, maintenanceEnabled: false, maintenanceMessage: null,
  canExtendTimer: false, timerExtensionMinutes: 15, timerExtensionRemainingMinutes: 60, canViewLogs: false
}
const snapshot: FriendSnapshot = {
  mode: 'Friend', state: 'Connected', detail: 'Private detail that must not become help.', endpoint: 'https://192.0.2.10:5131',
  lastConnectedUtc: '2026-10-08T15:00:00Z', remoteControlsEnabled: true, canStart: true, canStop: true,
  connectionId: 'connection', connections: null, profiles: [profile], hostCapabilities: ['server-logs-v1'], protocolCompatible: true
}

describe('Friend state and permission explanations', () => {
  it('distinguishes paused controls from disconnection and preserves separate read access', () => {
    const paused = { ...snapshot, state: 'Disabled', remoteControlsEnabled: false, profiles: [{ ...profile, canViewLogs: true }] }
    expect(friendConnectionExplanation(paused, nowMs)).toMatchObject({ state: 'paused', currentAccess: true })
    expect(effectiveFriendPermissions(paused, profile, nowMs).actions).toMatchObject({
      start: { available: false }, stop: { available: false }, logs: { available: true }
    })
    const disconnected = friendConnectionExplanation({ ...paused, state: 'Disconnected/Unknown' }, nowMs)
    expect(disconnected).toMatchObject({ state: 'disconnected', currentAccess: false })
    expect(disconnected.detail).toContain('Unknown')
    expect(disconnected.detail).not.toContain('turn remote controls back on')
  })

  it.each([
    ['AccessExpired', 'access-ended'], ['Revoked', 'removed'], ['ApprovalPending', 'approval'],
    ['HostIdentityMismatch', 'identity'], ['CredentialExpired', 'credential'], ['HostPortClosed', 'disconnected']
  ])('explains %s without reflecting server detail or offering permission by retry', (connectionCode, state) => {
    const explanation = friendConnectionExplanation({ ...snapshot, connectionCode }, nowMs)
    expect(explanation.state).toBe(state)
    expect(explanation.currentAccess).toBe(false)
    expect(explanation.detail).not.toContain(snapshot.detail)
    expect(effectiveFriendPermissions({ ...snapshot, connectionCode }, profile, nowMs).actions.start.available).toBe(false)
  })

  it('keeps owner-ended access separate from renewable credentials', () => {
    const explanation = friendConnectionExplanation({ ...snapshot, connectionCode: 'AccessExpired' }, nowMs)
    expect(explanation.detail).toContain('extend or clear your access deadline')
    expect(explanation.detail).toContain('new code is not required')
  })

  it('does not infer exact-server permission from global grants or a removed/stale card', () => {
    const denied = { ...profile, state: 'Offline', canStart: false, canViewLogs: false }
    const permissions = effectiveFriendPermissions({ ...snapshot, profiles: [denied] }, profile, nowMs)
    expect(permissions.actions.start).toMatchObject({ available: false, reason: 'The Host has not granted Start for this server.' })
    expect(permissions.actions.logs.available).toBe(false)
    expect(effectiveFriendPermissions({ ...snapshot, profiles: [] }, profile, nowMs).actions.start.available).toBe(false)
    expect(effectiveFriendPermissions(snapshot, profile, nowMs + 46_000).actions.stop.available).toBe(false)
  })

  it.each([null, 1, -1])('never describes Stop or Restart as available at count %s', onlinePlayers => {
    const current = { ...profile, onlinePlayers, canStopNow: true, canRestartNow: true }
    const permissions = effectiveFriendPermissions({ ...snapshot, profiles: [current] }, current, nowMs)
    expect(permissions.actions.stop.available).toBe(false)
    expect(permissions.actions.restart.available).toBe(false)
  })

  it('retains the Host safety flags at zero and derives Restart from both grants', () => {
    const blocked = { ...profile, canStopNow: false, canRestartNow: false }
    expect(effectiveFriendPermissions({ ...snapshot, profiles: [blocked] }, blocked, nowMs).actions.stop.available).toBe(false)
    const noStart = { ...profile, canStart: false }
    expect(effectiveFriendPermissions({ ...snapshot, profiles: [noStart] }, noStart, nowMs).actions.restart.available).toBe(false)
  })

  it('explains maintenance and independent add-time/log grants', () => {
    const current = { ...profile, maintenanceEnabled: true, canExtendTimer: true, canViewLogs: true }
    const permissions = effectiveFriendPermissions({ ...snapshot, profiles: [current] }, current, nowMs)
    expect(permissions.summary).toBe('Start · Stop · Add time · View logs')
    expect(permissions.actions.extend.available).toBe(false)
    expect(permissions.actions.logs.available).toBe(true)
  })

  it('offers Retry only for an unverified saved route, with fixed text', () => {
    const onRetry = vi.fn()
    const view = render(<FriendConnectionExplanation snapshot={{ ...snapshot, state: 'Disconnected/Unknown' }} nowMs={nowMs} onRetry={onRetry} />)
    fireEvent.click(screen.getByRole('button', { name: 'Retry saved connection' }))
    expect(onRetry).toHaveBeenCalledOnce()
    expect(view.container.textContent).not.toContain(snapshot.detail)
    view.rerender(<FriendConnectionExplanation snapshot={{ ...snapshot, state: 'Disabled', remoteControlsEnabled: false }} nowMs={nowMs} onRetry={onRetry} />)
    expect(screen.queryByRole('button', { name: 'Retry saved connection' })).not.toBeInTheDocument()
  })
})

describe('the composed play flow', () => {
  it('keeps a pending or single failed count check neutral without enabling Stop', () => {
    const onStop = vi.fn()
    const pending = { ...profile, onlinePlayers: null, playerCountRefreshing: true, playerCountFailureCount: 0 }
    const props = { profile: pending, nowMs, onStop, showServerNotifications: false }
    const view = render(<FriendPlayFlow {...props} snapshot={{ ...snapshot, profiles: [pending] }} />)
    expect(view.container.querySelector('.warning-text')).toBeNull()
    expect(screen.getByText('Checking players. Stop waits for a fresh count of zero.')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Stop server' })).toBeDisabled()
    const first = { ...pending, playerCountRefreshing: false, playerCountFailureCount: 1 }
    view.rerender(<FriendPlayFlow {...props} profile={first} snapshot={{ ...snapshot, profiles: [first] }} />)
    expect(view.container.querySelector('.warning-text')).toBeNull()
    expect(screen.getByRole('button', { name: 'Stop server' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Stop server' }))
    expect(onStop).not.toHaveBeenCalled()
  })

  it('places local hosting beside Start without granting remote Start permission', () => {
    const stopped = { ...profile, state: 'Offline', joinAddress: null, canStart: false }
    const hostHere = vi.fn()
    render(<FriendPlayFlow snapshot={{ ...snapshot, profiles: [stopped] }} profile={stopped} nowMs={nowMs}
      onStart={vi.fn()} showServerNotifications={false}
      localHostingAction={<button onClick={hostHere}>Host on this PC</button>} />)
    const start = screen.getByRole('button', { name: 'Start server' })
    const local = screen.getByRole('button', { name: 'Host on this PC' })
    expect(start).toBeDisabled()
    expect(start.parentElement).toBe(local.parentElement)
    fireEvent.click(local)
    expect(hostHere).toHaveBeenCalledOnce()
  })

  it('retains the Unknown-player Stop guard after dismissal and repeats the warning for another connection', () => {
    const onStop = vi.fn()
    const unknown = { ...profile, onlinePlayers: null, joinAddress: null, playerCountFailureCount: 2 }
    const props = { profile: unknown, nowMs, onStop, showServerNotifications: false }
    const current = { ...snapshot, profiles: [unknown] }
    const view = render(<FriendPlayFlow {...props} snapshot={current} />)
    expect(screen.getByRole('button', { name: 'Stop server' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss notification' }))
    expect(screen.queryByText(/Player count is Unknown\. Refresh players/, { selector: '.warning-text' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Stop server' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Stop server' }))
    expect(onStop).not.toHaveBeenCalled()
    view.rerender(<FriendPlayFlow {...props} snapshot={{ ...current, lastConnectedUtc: '2026-10-08T15:00:05Z' }} />)
    expect(screen.queryByText(/Player count is Unknown\. Refresh players/, { selector: '.warning-text' })).not.toBeInTheDocument()
    view.rerender(<FriendPlayFlow {...props} snapshot={{ ...current, connectionId: 'another-Host' }} />)
    expect(screen.getByText(/Player count is Unknown\. Refresh players/, { selector: '.warning-text' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Stop server' })).toBeDisabled()
    expect(onStop).not.toHaveBeenCalled()
  })

  it('loads local scoped notification settings only after the owner opens that disclosure', async () => {
    const fetcher = vi.fn<typeof fetch>(async () => new Response(JSON.stringify({ quietMode: false, allowedEvents: ['Backup'], servers: [],
      systemState: 'AcceptsNotifications', systemAllowsNotifications: true }), { status: 200, headers: { 'Content-Type': 'application/json' } }))
    vi.stubGlobal('fetch', fetcher)
    render(<FriendPlayFlow snapshot={snapshot} profile={profile} nowMs={nowMs} />)
    expect(fetcher).not.toHaveBeenCalled()
    const disclosure = screen.getByText('Server notifications').closest('details')!
    disclosure.open = true
    fireEvent(disclosure, new Event('toggle'))
    expect(await screen.findByRole('heading', { name: `Notifications for ${profile.name}` })).toBeInTheDocument()
    await waitFor(() => expect(screen.getByRole('combobox', { name: 'Server alerts' })).toBeEnabled())
    expect(fetcher).toHaveBeenCalledOnce()
    expect(fetcher.mock.calls[0][0]).toBe('/api/local/desktop/notifications')
    expect(screen.queryByRole('checkbox', { name: /Quiet mode/ })).not.toBeInTheDocument()
  })
  it('keeps occupied Stop blockers visible while Connection Doctor stays in server tools', () => {
    const occupied = { ...profile, onlinePlayers: 1 }
    const doctor = vi.fn()
    render(<FriendPlayFlow snapshot={{ ...snapshot, profiles: [occupied] }} profile={occupied} nowMs={nowMs}
      onStop={vi.fn()} onOpenConnectionDoctor={doctor} />)
    expect(screen.getByRole('heading', { name: 'Play' })).toBeInTheDocument()
    expect(screen.getAllByRole('status').find(element => element.textContent?.includes('Players are online.'))).toHaveTextContent('Wait until the server reports zero players.')
    expect(screen.getByRole('button', { name: 'Open Connection Doctor' })).not.toBeVisible()
    fireEvent.click(screen.getByLabelText('Server tools and connection help'))
    fireEvent.click(screen.getByRole('button', { name: 'Open Connection Doctor' }))
    expect(doctor).toHaveBeenCalledOnce()
  })
  it('groups one explicit Start, then waiting, then existing game/copy/manual steps without automatic actions', () => {
    const fetcher = vi.fn(); vi.stubGlobal('fetch', fetcher)
    const onStart = vi.fn(), onRefresh = vi.fn()
    const offline = { ...profile, state: 'Offline', joinAddress: null }
    const view = render(<FriendPlayFlow snapshot={{ ...snapshot, profiles: [offline] }} profile={offline}
      nowMs={nowMs} onStart={onStart} onRefresh={onRefresh} startNotice={<p>Start route notice</p>}
      connectionDetails={<p>Masked connection controls</p>} requirements={<p>Requirements slot</p>} />)
    expect(onStart).not.toHaveBeenCalled()
    expect(onRefresh).not.toHaveBeenCalled()
    expect(fetcher).not.toHaveBeenCalled()
    expect(screen.getAllByRole('button', { name: 'Start server' })).toHaveLength(1)
    fireEvent.click(screen.getByRole('button', { name: 'Start server' }))
    expect(onStart).toHaveBeenCalledOnce()
    const starting = { ...profile, state: 'Starting' }
    view.rerender(<FriendPlayFlow snapshot={{ ...snapshot, profiles: [starting] }} profile={starting} nowMs={nowMs} onStart={onStart} />)
    expect(screen.queryByRole('button', { name: 'Open game' })).not.toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Wait for its readiness observation')
    view.rerender(<FriendPlayFlow snapshot={snapshot} profile={profile} nowMs={nowMs}
      connectionDetails={<p>Masked connection controls</p>} requirements={<p>Requirements slot</p>} />)
    expect(screen.getByRole('button', { name: 'Open game' })).toBeInTheDocument()
    expect(screen.getByText('Masked connection controls')).toBeInTheDocument()
    expect(screen.getByText('Requirements slot')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Open game' }).compareDocumentPosition(screen.getByText('Requirements slot')) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(screen.getByText('Masked connection controls').compareDocumentPosition(screen.getByText('Requirements slot')) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(screen.getByText('How to join in Valheim')).toBeInTheDocument()
    expect(screen.getByText('How to join in Valheim').closest('details')).toBe(screen.getByLabelText('How to join in the game').closest('details'))
    expect(fetcher).not.toHaveBeenCalled()
  })

  it('retains a Listening warning, permits manual play with paused controls, and hides stale join actions', () => {
    const listening = { ...profile, kind: 'Terraria', state: 'Listening', onlinePlayers: null }
    const paused = { ...snapshot, state: 'Disabled', remoteControlsEnabled: false, profiles: [listening] }
    const view = render(<FriendPlayFlow snapshot={paused} profile={listening} nowMs={nowMs} />)
    expect(screen.getByRole('status')).toHaveTextContent('readiness, player count and a real join remain unverified')
    expect(screen.getByRole('button', { name: 'Open game' })).toBeEnabled()
    view.rerender(<FriendPlayFlow snapshot={paused} profile={listening} nowMs={nowMs + 46_000} />)
    expect(screen.queryByRole('button', { name: 'Open game' })).not.toBeInTheDocument()
    expect(screen.getByText(/Your access:.*Unverified/)).toBeInTheDocument()
  })

  it('does not offer an actionable Start through maintenance, denied permission or an active operation', () => {
    const onStart = vi.fn()
    const offline = { ...profile, state: 'Offline', maintenanceEnabled: true }
    const view = render(<FriendPlayFlow snapshot={{ ...snapshot, profiles: [offline] }} profile={offline} nowMs={nowMs} onStart={onStart} />)
    expect(screen.getByRole('button', { name: 'Start server' })).toBeDisabled()
    expect(screen.getByRole('status')).toHaveTextContent('paused lifecycle controls for maintenance')
    fireEvent.click(screen.getByRole('button', { name: 'Start server' }))
    expect(onStart).not.toHaveBeenCalled()
    const denied = { ...offline, maintenanceEnabled: false, canStart: false }
    view.rerender(<FriendPlayFlow snapshot={{ ...snapshot, profiles: [denied] }} profile={denied} nowMs={nowMs} onStart={onStart} />)
    expect(screen.getByRole('button', { name: 'Start server' })).toBeDisabled()
    expect(screen.getByRole('status')).toHaveTextContent('The Host has not granted Start for this server.')
    const pending = { ...denied, canStart: true, operation: { id: 'operation', action: 'start', state: 'Pending' as const,
      ok: null, code: 'Pending', message: '', requestedUtc: '2026-10-08T15:00:00Z', completedUtc: null } }
    view.rerender(<FriendPlayFlow snapshot={{ ...snapshot, profiles: [pending] }} profile={pending} nowMs={nowMs} onStart={onStart} />)
    expect(screen.getByRole('button', { name: 'Start server' })).toBeDisabled()
  })

  it('keeps missing-address guidance visible and permits manual continuation despite unknown requirements', () => {
    const missingAddress = { ...profile, joinAddress: null }
    const view = render(<FriendPlayFlow snapshot={{ ...snapshot, profiles: [missingAddress] }} profile={missingAddress} nowMs={nowMs}
      requirements={<p>Version: Unknown · Add-ons: Unknown</p>} />)
    expect(screen.getByText(/Wait for the Host to share a current game address/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Open game' })).not.toBeInTheDocument()
    view.rerender(<FriendPlayFlow snapshot={snapshot} profile={profile} nowMs={nowMs}
      requirements={<p>Version: Unknown · Add-ons: Unknown</p>} connectionDetails={<p>Manual copy controls</p>} />)
    expect(screen.getByRole('button', { name: 'Open game' })).toBeEnabled()
    expect(screen.getByText('Manual copy controls')).toBeInTheDocument()
    expect(screen.getByText('Version: Unknown · Add-ons: Unknown')).toBeInTheDocument()
  })

  it('leads with game and connection actions while keeping supporting requirements findable', () => {
    render(<FriendPlayFlow snapshot={snapshot} profile={profile} nowMs={nowMs}
      requirements={<p>Version checks</p>} connectionDetails={<section aria-label="Join details">Copy connection</section>} />)
    const details = screen.getByRole('region', { name: 'Join details' })
    expect(screen.getByRole('button', { name: 'Open game' }).compareDocumentPosition(details) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(details.compareDocumentPosition(screen.getByText('Version checks')) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(screen.getByLabelText('How to join in the game').closest('details')).not.toHaveAttribute('open')
  })

  it('keeps granted Stop visible and blocks it for an occupied, stale or paused Host', () => {
    const stop = vi.fn()
    const occupied = { ...profile, onlinePlayers: 1 }
    const view = render(<FriendPlayFlow snapshot={{ ...snapshot, profiles: [occupied] }} profile={occupied} nowMs={nowMs} onStop={stop} />)
    expect(screen.getByRole('button', { name: 'Stop server' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Stop server' }))
    expect(stop).not.toHaveBeenCalled()
    view.rerender(<FriendPlayFlow snapshot={snapshot} profile={profile} nowMs={nowMs + 46_000} onStop={stop} />)
    expect(screen.queryByRole('button', { name: 'Stop server' })).not.toBeInTheDocument()
    view.rerender(<FriendPlayFlow snapshot={{ ...snapshot, state: 'Disabled', remoteControlsEnabled: false }} profile={profile} nowMs={nowMs} onStop={stop} />)
    expect(screen.getByRole('button', { name: 'Stop server' })).toBeDisabled()
    view.rerender(<FriendPlayFlow snapshot={snapshot} profile={profile} nowMs={nowMs} onStop={stop} />)
    fireEvent.click(screen.getByRole('button', { name: 'Stop server' }))
    expect(stop).toHaveBeenCalledOnce()
  })
})

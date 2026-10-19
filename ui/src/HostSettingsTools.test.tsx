import type { ComponentProps } from 'react'
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import hostWire from '../../contracts/host-snapshot.v1.json'
import { parseSnapshot, type HostSnapshot } from './contracts'
import { HostSettingsTools } from './HostSettingsTools'

vi.mock('./ConnectionDoctor', () => ({ ConnectionDoctor: ({ onOpenDiagnostics }: { onOpenDiagnostics: () => void }) =>
  <section aria-label="Connection evidence"><button onClick={onOpenDiagnostics}>Open diagnostics</button></section> }))
vi.mock('./RemoteRehearsal', () => ({ HostRemoteRehearsal: () => <p>Rehearsal controls</p> }))

beforeEach(() => {
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value: function (this: HTMLDialogElement) { this.setAttribute('open', '') } })
  Object.defineProperty(HTMLDialogElement.prototype, 'close', { configurable: true, value: function (this: HTMLDialogElement) { this.removeAttribute('open') } })
})

function props(section: 'stop' | 'network' | 'advanced' = 'stop'): ComponentProps<typeof HostSettingsTools> {
  return { section, snapshot: parseSnapshot(structuredClone(hostWire)) as HostSnapshot,
    companion: null, ports: null, routeCheck: null, previousRouteVerdict: false, routeDiscovery: null,
    selectedProfileId: hostWire.settings.profiles[0].id, busy: false, dirty: false, checkingPorts: false,
    checkingRoute: false, detectingAddress: false, friendAppStatus: 'off', addressError: null, isStaging: false, notice: null, moveKit: null,
    onClearNotice: vi.fn(), onDraftStateChange: vi.fn(), onSave: vi.fn(async () => true), onAutoShutdown: vi.fn(), onRefresh: vi.fn(),
    onTestRoute: vi.fn(), onDetectAddress: vi.fn(), onOpenAccess: vi.fn(), onOpenDiagnostics: vi.fn(),
    onCertificate: vi.fn(), onInspectMoveKit: vi.fn(), onNewHost: vi.fn() }
}

describe('Host settings cards and editors', () => {
  it('does not advertise removed PCs or their count in Host settings', () => {
    const input = props('advanced')
    input.companion = { listenerActive: false, listenerState: 'Off', listenerWarning: null,
      endpoint: '', fingerprint: null, certificates: null, route: { mode: 'DirectInternet', address: '' }, stopSafety: {},
      devices: [{ id: 'removed', profileId: 'one', assignedProfileIds: [], name: 'Removed laptop',
        canStart: false, canStop: false, canExtendTimer: false, canViewLogs: false, revoked: true, paired: true,
        approvalPending: false, credentialExpiresUtc: null, accessExpiresUtc: null, accessExpired: false,
        lastHeartbeatUtc: null, serverPermissions: [] }] }
    render(<HostSettingsTools {...input} />)
    expect(screen.queryByText(/Revoked Friend PCs|Removed laptop|1 removed/)).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /View removed PCs/ })).not.toBeInTheDocument()
  })
  it('opens notifications for the selected Host server and keeps its dialog open while the scoped save is pending', async () => {
    const input = props('advanced')
    const notificationPreferences = { quietMode: false, allowedEvents: ['Backup'], servers: [], systemState: 'AcceptsNotifications', systemAllowsNotifications: true }
    const response = () => new Response(JSON.stringify(notificationPreferences), { status: 200, headers: { 'Content-Type': 'application/json' } })
    let finish: ((value: Response) => void) | undefined
    const fetcher = vi.fn<typeof fetch>(async (_url, options) => options?.method === 'PUT'
      ? new Promise<Response>(resolve => { finish = resolve }) : response())
    vi.stubGlobal('fetch', fetcher)
    render(<HostSettingsTools {...input} />)
    expect(fetcher).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Server notifications Edit server notifications' }))
    const profile = input.snapshot.settings.profiles.find(item => item.id === input.selectedProfileId)!
    expect(await screen.findByRole('heading', { name: `Notifications for ${profile.name}` })).toBeInTheDocument()
    fireEvent.change(await screen.findByRole('combobox', { name: 'Server alerts' }), { target: { value: 'off' } })
    await waitFor(() => expect(fetcher).toHaveBeenCalledTimes(2))
    expect(JSON.parse(fetcher.mock.calls[1][1]!.body as string)).toEqual({ profileId: profile.id, connectionId: null, allowedEvents: [] })
    expect(screen.getByRole('button', { name: 'Done' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Close Server notifications' })).toBeDisabled()
    await act(async () => finish?.(response()))
    expect(screen.getByRole('button', { name: 'Done' })).toBeEnabled()
    expect(input.onSave).not.toHaveBeenCalled()
  })
  it('keeps the hosting power choice in Shutdown and leaves unknown occupancy warnings intact', () => {
    const keepAwake = { enabled: false, detail: 'Power request inactive.', onChange: vi.fn() }
    render(<HostSettingsTools {...props()} keepAwake={keepAwake} />)
    fireEvent.click(screen.getByRole('checkbox', { name: /Keep Windows awake/ }))
    expect(keepAwake.onChange).toHaveBeenCalledExactlyOnceWith(true)
    expect(screen.getByText('Power request inactive.')).toBeInTheDocument()
    expect(screen.getByText(/unknown count cancel/)).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: 'Shutdown' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Edit extensions|Review Stop safety/ })).not.toBeInTheDocument()
  })
  it('keeps timer fields out of the overview, cancels locally, and reopens saved values', () => {
    const input = props()
    render(<HostSettingsTools {...input} />)
    expect(screen.queryByRole('spinbutton')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Empty-server countdown Edit countdown' }))
    const field = screen.getByRole('spinbutton', { name: /Wait after/ })
    fireEvent.change(field, { target: { value: '37' } })
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(input.onSave).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Empty-server countdown Edit countdown' }))
    expect(screen.getByRole('spinbutton', { name: /Wait after/ })).toHaveValue(input.snapshot.settings.idleMinutes)
  })

  it('saves only the changed timer fields and retains the draft after a failed save', async () => {
    const input = props()
    vi.mocked(input.onSave).mockResolvedValueOnce(false).mockResolvedValueOnce(true)
    render(<HostSettingsTools {...input} />)
    fireEvent.click(screen.getByRole('button', { name: 'Empty-server countdown Edit countdown' }))
    fireEvent.change(screen.getByRole('spinbutton', { name: /Wait after/ }), { target: { value: '37' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save timer' }))
    await waitFor(() => expect(input.onSave).toHaveBeenCalledWith({ idleMinutes: 37 }))
    expect(screen.getByRole('dialog', { name: 'Empty-server countdown' })).toBeInTheDocument()
    expect(screen.getByRole('spinbutton', { name: /Wait after/ })).toHaveValue(37)
    fireEvent.click(screen.getByRole('button', { name: 'Save timer' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('rejects invalid minutes and keeps optional extension controls inside the same countdown editor', async () => {
    const input = props()
    render(<HostSettingsTools {...input} />)
    fireEvent.click(screen.getByRole('button', { name: 'Empty-server countdown Edit countdown' }))
    fireEvent.change(screen.getByRole('spinbutton', { name: /Wait after/ }), { target: { value: '0' } })
    expect(screen.getByRole('button', { name: 'Save timer' })).toBeDisabled()
    fireEvent.change(screen.getByRole('spinbutton', { name: /Wait after/ }), { target: { value: '20' } })
    expect(screen.queryByRole('spinbutton', { name: /Minutes added per request/ })).not.toBeInTheDocument()
    fireEvent.click(screen.getByText('Allow friends to add time'))
    fireEvent.change(await screen.findByRole('spinbutton', { name: /Minutes added per request/ }), { target: { value: '20' } })
    fireEvent.change(screen.getByRole('spinbutton', { name: /Maximum minutes friends can add/ }), { target: { value: '19' } })
    expect(screen.getByRole('button', { name: 'Save timer' })).toBeDisabled()
    expect(screen.getByRole('status')).toHaveTextContent('maximum must allow at least one request')
    fireEvent.click(within(screen.getByRole('status')).getByRole('button', { name: 'Dismiss notification' }))
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Save timer' })).toBeDisabled()
    fireEvent.change(screen.getByRole('spinbutton', { name: /Maximum minutes friends can add/ }), { target: { value: '20' } })
    fireEvent.change(screen.getByRole('spinbutton', { name: /Maximum minutes friends can add/ }), { target: { value: '19' } })
    expect(screen.getByRole('status')).toHaveTextContent('maximum must allow at least one request')
    expect(input.onSave).not.toHaveBeenCalled()
  })

  it('keeps checks on demand and a dismissed TCP result hidden through polling until a later route test', async () => {
    const input = props('network')
    const routeCheck = { state: 'Not reachable' as const, detail: 'The Friend app TCP port could not be reached.',
      port: input.snapshot.settings.companionPort, checkedUtc: '2026-10-11T12:00:00Z' }
    const view = render(<HostSettingsTools {...input} routeCheck={routeCheck} />)
    expect(screen.queryByRole('button', { name: 'Troubleshoot connection' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByText('Troubleshooting'))
    fireEvent.click(await screen.findByRole('button', { name: 'Troubleshoot connection' }))
    expect(screen.queryByRole('button', { name: 'Refresh public address' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByText('Address and port check details'))
    await screen.findByRole('button', { name: 'Refresh public address' })
    fireEvent.click(within(screen.getByRole('status')).getByRole('button', { name: 'Dismiss notification' }))
    const updated = { ...routeCheck, detail: 'Updated display details for the same test.' }
    view.rerender(<HostSettingsTools {...input} routeCheck={updated} />)
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
    view.rerender(<HostSettingsTools {...input} routeCheck={{ ...updated, checkedUtc: '2026-10-11T12:01:00Z' }} />)
    expect(screen.getByRole('status')).toHaveTextContent(updated.detail)
    expect(input.onTestRoute).not.toHaveBeenCalled()
    expect(input.onSave).not.toHaveBeenCalled()
  })

  it('keeps local timer edits across snapshot refresh without sending unrelated settings', async () => {
    const input = props()
    const saved = structuredClone(input.snapshot.settings)
    const view = render(<HostSettingsTools {...input} />)
    fireEvent.click(screen.getByRole('button', { name: 'Empty-server countdown Edit countdown' }))
    fireEvent.change(screen.getByRole('spinbutton', { name: /Wait after/ }), { target: { value: '41' } })
    const updated = structuredClone(input.snapshot)
    updated.settings.friendTimerExtensionMaximumMinutes = 99
    view.rerender(<HostSettingsTools {...input} snapshot={updated} />)
    expect(screen.getByRole('spinbutton', { name: /Wait after/ })).toHaveValue(41)
    fireEvent.click(screen.getByRole('button', { name: 'Save timer' }))
    await waitFor(() => expect(input.onSave).toHaveBeenCalledWith({ idleMinutes: 41 }))
    expect(input.snapshot.settings).toEqual(saved)
  })

  it('reviews a selected route without applying it until Save', async () => {
    const input = props('network')
    render(<HostSettingsTools {...input} />)
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Address and ports Edit network' }))
    fireEvent.change(screen.getByRole('combobox', { name: /Network route/ }), { target: { value: 'PrivateMesh' } })
    fireEvent.change(screen.getByRole('textbox', { name: 'Network IPv4 address' }), { target: { value: '100.64.0.2' } })
    expect(input.onSave).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(input.onSave).toHaveBeenCalledWith({ connectionRoute: { mode: 'PrivateMesh', address: '100.64.0.2' },
      companionEndpoint: `https://100.64.0.2:${input.snapshot.settings.companionPort}`, companionBindAddress: '100.64.0.2' }))
  })

  it('edits a port in the same Network editor while custom binding fields stay unmounted until requested', async () => {
    const input = props('network')
    render(<HostSettingsTools {...input} />)
    fireEvent.click(screen.getByRole('button', { name: 'Address and ports Edit network' }))
    expect(screen.queryByRole('textbox', { name: 'Bind IP' })).not.toBeInTheDocument()
    expect(screen.queryByRole('textbox', { name: 'Custom secure address' })).not.toBeInTheDocument()
    fireEvent.change(screen.getByRole('spinbutton', { name: /^Friend app TCP port/u }), { target: { value: '5140' } })
    fireEvent.click(screen.getByText('Advanced address settings'))
    expect(await screen.findByRole('textbox', { name: 'Bind IP' })).toHaveValue(input.snapshot.settings.companionBindAddress)
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(input.onSave).toHaveBeenCalledWith({ companionPort: 5140,
      ...(input.snapshot.settings.companionEndpoint ? { companionEndpoint: new URL(input.snapshot.settings.companionEndpoint).origin.replace(/:\d+$/u, ':5140') } : {}) }))
    expect(input.onTestRoute).not.toHaveBeenCalled()
  })

  it('saves only edited extension values while unrelated timer settings refresh in the background', async () => {
    const input = props()
    const view = render(<HostSettingsTools {...input} />)
    fireEvent.click(screen.getByRole('button', { name: 'Empty-server countdown Edit countdown' }))
    fireEvent.click(screen.getByText('Allow friends to add time'))
    fireEvent.change(await screen.findByRole('spinbutton', { name: /Minutes added per request/ }), { target: { value: '7' } })
    const updated = structuredClone(input.snapshot)
    updated.settings.idleMinutes = 42
    view.rerender(<HostSettingsTools {...input} snapshot={updated} />)
    fireEvent.click(screen.getByRole('button', { name: 'Save timer' }))
    await waitFor(() => expect(input.onSave).toHaveBeenCalledExactlyOnceWith({ friendTimerExtensionMinutes: 7 }))
  })

  it('opens read-only connection evidence without a probe, and closes it before navigating', async () => {
    const input = props('network')
    render(<HostSettingsTools {...input} />)
    expect(screen.queryByRole('region', { name: 'Connection evidence' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByText('Troubleshooting'))
    fireEvent.click(await screen.findByRole('button', { name: 'Troubleshoot connection' }))
    expect(screen.getByRole('dialog', { name: 'Troubleshoot connection' })).toBeInTheDocument()
    expect(input.onTestRoute).not.toHaveBeenCalled()
    expect(input.onSave).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Open diagnostics' }))
    expect(input.onOpenDiagnostics).toHaveBeenCalledOnce()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('keeps advanced fields scoped and prevents closing an in-flight Save', () => {
    const input = props('network')
    const view = render(<HostSettingsTools {...input} />)
    fireEvent.click(screen.getByRole('button', { name: 'Address and ports Edit network' }))
    const dialog = screen.getByRole('dialog', { name: 'Address and ports' })
    fireEvent.change(within(dialog).getByRole('spinbutton', { name: /^Friend app TCP port/u }), { target: { value: '5133' } })
    view.rerender(<HostSettingsTools {...input} busy />)
    expect(within(dialog).getByRole('button', { name: 'Cancel' })).toBeDisabled()
    const cancel = new Event('cancel', { cancelable: true })
    fireEvent(dialog, cancel)
    expect(cancel.defaultPrevented).toBe(true)
    expect(dialog).toHaveAttribute('open')
  })
})

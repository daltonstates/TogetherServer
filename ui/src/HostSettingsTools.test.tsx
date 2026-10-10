import type { ComponentProps } from 'react'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
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

  it('rejects invalid minutes and extension maxima that cannot cover one increment', () => {
    const input = props()
    render(<HostSettingsTools {...input} />)
    fireEvent.click(screen.getByRole('button', { name: 'Empty-server countdown Edit countdown' }))
    fireEvent.change(screen.getByRole('spinbutton', { name: /Wait after/ }), { target: { value: '0' } })
    expect(screen.getByRole('button', { name: 'Save timer' })).toBeDisabled()
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))
    fireEvent.click(screen.getByRole('button', { name: 'Friend timer extensions Edit extensions' }))
    fireEvent.change(screen.getByRole('spinbutton', { name: /Friend extension increment/ }), { target: { value: '20' } })
    fireEvent.change(screen.getByRole('spinbutton', { name: /Friend extension maximum/ }), { target: { value: '19' } })
    expect(screen.getByRole('button', { name: 'Save timer' })).toBeDisabled()
    expect(screen.getByRole('status')).toHaveTextContent('maximum must cover at least one extension increment')
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
    fireEvent.click(screen.getByRole('button', { name: 'Friend connection route Edit connection route' }))
    fireEvent.change(screen.getByRole('combobox', { name: /Friend route/ }), { target: { value: 'PrivateMesh' } })
    fireEvent.change(screen.getByRole('textbox', { name: 'Selected route IPv4' }), { target: { value: '100.64.0.2' } })
    expect(input.onSave).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }))
    await waitFor(() => expect(input.onSave).toHaveBeenCalledWith({ connectionRoute: { mode: 'PrivateMesh', address: '100.64.0.2' },
      companionEndpoint: `https://100.64.0.2:${input.snapshot.settings.companionPort}`, companionBindAddress: '100.64.0.2' }))
  })

  it('opens read-only connection evidence without a probe, and closes it before navigating', () => {
    const input = props('network')
    render(<HostSettingsTools {...input} />)
    expect(screen.queryByRole('region', { name: 'Connection evidence' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Connection Doctor Open Connection Doctor' }))
    expect(screen.getByRole('dialog', { name: 'Connection Doctor' })).toBeInTheDocument()
    expect(input.onTestRoute).not.toHaveBeenCalled()
    expect(input.onSave).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('button', { name: 'Open diagnostics' }))
    expect(input.onOpenDiagnostics).toHaveBeenCalledOnce()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('keeps advanced fields scoped and prevents closing an in-flight Save', () => {
    const input = props('advanced')
    const view = render(<HostSettingsTools {...input} />)
    fireEvent.click(screen.getByRole('button', { name: 'Advanced network settings Edit network settings' }))
    const dialog = screen.getByRole('dialog', { name: 'Advanced network settings' })
    fireEvent.change(within(dialog).getByRole('spinbutton', { name: 'Friend app TCP port' }), { target: { value: '5133' } })
    view.rerender(<HostSettingsTools {...input} busy />)
    expect(within(dialog).getByRole('button', { name: 'Cancel' })).toBeDisabled()
    const cancel = new Event('cancel', { cancelable: true })
    fireEvent(dialog, cancel)
    expect(cancel.defaultPrevented).toBe(true)
    expect(dialog).toHaveAttribute('open')
  })
})

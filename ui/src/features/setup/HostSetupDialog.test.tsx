import { createRef, type ComponentProps } from 'react'
import { act, fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { HostSetupDialog, getSetupIssues } from './HostSetupDialog'
import type { HostSnapshot, Settings } from '../../contracts'
import type { Profile } from '../../GameProfile'

const profile: Profile = { id: '11111111-1111-4111-8111-111111111111', kind: 'Valheim', name: '', serverName: '', crossplay: false,
  publicListing: false, worldId: '', worldSource: 'New', worldDirectory: '', gamePort: 0, executablePath: '' }
function propsFor(editedProfile: Profile): ComponentProps<typeof HostSetupDialog> {
  const settings = { profiles: [editedProfile] } as Settings
  return { dialogRef: createRef<HTMLDialogElement>(), snapshot: { mode: 'Host', passwordConfigured: {}, managedWorldsRoot: 'C:\\synthetic\\managed' } as unknown as HostSnapshot,
    draft: settings, savedProfiles: [], editedProfile, notice: null, pending: '', dirty: true, setupStep: 'review',
    setupIssues: getSetupIssues(editedProfile, false, '', undefined, false), stepIssues: [], discovery: null, minecraftDiscovery: null,
    sourceRoots: {}, passwords: {}, showPasswords: {}, minecraftSetupMode: {}, minecraftTerms: {}, customScripts: {}, customScriptsSaved: {}, customScriptsLoading: {},
    customScriptsChanged: false, dataRecoveryBlocked: false, freshWorldsOnly: false,
    onCancel: vi.fn(), onFinishLater: vi.fn(), onAddProfile: vi.fn(), onStepChange: vi.fn(), onChangeGameKind: vi.fn(), onUpdateProfile: vi.fn(),
    onImportWorld: vi.fn(), onBrowseWorld: vi.fn(), onSourceRootChange: vi.fn(), onPasswordChange: vi.fn(), onShowPasswordChange: vi.fn(),
    onBrowseCustomDirectory: vi.fn(), onBrowseFactorio: vi.fn(), onBrowseTerraria: vi.fn(), onMinecraftSetupModeChange: vi.fn(), onBrowseMinecraft: vi.fn(),
    onApplyMinecraftInstallation: vi.fn(), onScanMinecraft: vi.fn(), onInstallMinecraft: vi.fn(), onMinecraftTermsChange: vi.fn(), onScanValheim: vi.fn(),
    onBrowseServer: vi.fn(), onEditCustomScripts: vi.fn(), onUpdateCustomPort: vi.fn(), onAddCustomPort: vi.fn(), onRemoveCustomPort: vi.fn(),
    onRemoveProfile: vi.fn(), onSave: vi.fn() }
}
function exposeDialog(props: ComponentProps<typeof HostSetupDialog>) {
  act(() => props.dialogRef.current!.setAttribute('open', ''))
}
describe('Host setup review', () => {
  it('shows every setup blocker and moves the owner to its editable field', () => {
    const props = propsFor(profile)
    const rendered = render(<HostSetupDialog {...props} />)
    exposeDialog(props)
    expect(screen.getByRole('region', { name: 'Setup blockers' }).querySelectorAll('li')).toHaveLength(6)
    fireEvent.click(screen.getByRole('button', { name: 'Name your new world.' }))
    expect(props.onStepChange).toHaveBeenCalledWith('world')
    rendered.rerender(<HostSetupDialog {...props} setupStep="world" />)
    expect(document.activeElement).toBe(screen.getByRole('textbox', { name: /^World name/u }))
    fireEvent.click(screen.getByRole('button', { name: 'Enter a game password.' }))
    expect(document.activeElement).toBe(screen.getByLabelText(/^Game password/u, { selector: 'input[type="password"]' }))
  })
  it('applies a full non-overlapping port suggestion without starting a server', () => {
    const saved: Profile = { ...profile, gamePort: 2456, name: 'Original', serverName: 'Original', worldId: 'original',
      worldDirectory: 'C:\\synthetic\\one', executablePath: 'C:\\synthetic\\valheim_server.exe' }
    const edited = { ...saved, id: '22222222-2222-4222-8222-222222222222', worldId: 'second', worldDirectory: 'C:\\synthetic\\two' }
    const props = { ...propsFor(edited), setupIssues: [], draft: { profiles: [saved, edited] } as Settings, savedProfiles: [saved] }
    render(<HostSetupDialog {...props} />)
    exposeDialog(props)
    fireEvent.click(screen.getByRole('button', { name: 'Apply suggested ports' }))
    expect(props.onUpdateProfile).toHaveBeenCalledWith(edited.id, { gamePort: 2458 })
    expect(props.onSave).not.toHaveBeenCalled()
  })
})

import { describe, expect, it, vi } from 'vitest'
import { ContractError, parseAppInstance, parseDataRecoveryView, parsePortDiagnostics, parseSettings, parseSnapshot, type Settings } from './contracts'
import { readSetupDraft, serializeSetupDraft } from './setupDraft'

const settings: Settings = {
  maxConcurrentServers: 1,
  idleMinutes: 15,
  friendTimerExtensionMinutes: 15,
  friendTimerExtensionMaximumMinutes: 60,
  autoShutdownEnabled: false,
  remoteControlsEnabled: false,
  companionListeningEnabled: false,
  companionBindAddress: '127.0.0.1',
  companionEndpoint: 'https://127.0.0.1:5131',
  companionPort: 5131,
  connectionRoute: { mode: 'DirectInternet', address: '' },
  publicGameIp: '',
  publicGameIpCheckedUtc: null,
  profiles: []
}

describe('runtime contracts', () => {
  it('accepts a valid Host snapshot and recovery view', () => {
    const recovery = {
      lifecycleBlocked: true,
      notices: [{ stateFile: 'runs.json', quarantinedFile: 'runs.invalid.json', reason: 'Invalid JSON',
        detectedUtc: '2026-09-24T12:00:00Z', blocksLifecycle: true }]
    }
    const snapshot = parseSnapshot({
      mode: 'Host',
      evidence: 'Recorded identity',
      settings,
      runs: [],
      passwordConfigured: {},
      managedWorldsRoot: 'C:\\TogetherServer\\worlds',
      recovery
    })

    expect(snapshot.mode).toBe('Host')
    expect(parseDataRecoveryView(recovery).lifecycleBlocked).toBe(true)
  })

  it('rejects invalid settings instead of trusting persisted JSON', () => {
    expect(() => parseSettings({ ...settings, companionPort: '5131' })).toThrow(ContractError)
  })

  it('validates the staging isolation contract', () => {
    const instance = parseAppInstance({
      kind: 'Staging', displayName: 'TogetherServer DEVELOPMENT', isStaging: true, freshWorldsOnly: true,
      startupAvailable: false, updatesAvailable: false, localPort: 5128, companionPort: 5132,
      valheimPort: 2458, minecraftJavaPort: 25566, minecraftBedrockPort: 19134,
      dataRoot: 'C:\\TogetherServer-Staging', dataIsolation: 'No production data is loaded.'
    })

    expect(instance.isStaging).toBe(true)
    expect(instance.freshWorldsOnly).toBe(true)
    expect(() => parseAppInstance({ ...instance, companionPort: '5132' })).toThrow(ContractError)
  })

  it('accepts an unconfigured Friend endpoint in development port diagnostics', () => {
    const diagnostics = parsePortDiagnostics({
      checkedUtc: '2026-09-25T22:00:00Z',
      games: [],
      control: {
        port: 5132,
        state: 'Off',
        detail: 'Friend connections are off.',
        remoteState: 'Not verified',
        remoteDetail: 'No paired Friend has a current authenticated heartbeat.',
        bindAddress: '0.0.0.0',
        bindScope: 'All IPv4 interfaces',
        endpoint: null,
        endpointState: 'Not configured',
        endpointDetail: 'Create an invite to set the Friend app address.',
        lanAddresses: [],
        lanForwardDetail: 'No local route selected.'
      }
    })

    expect(diagnostics.control.port).toBe(5132)
    expect(diagnostics.control.endpoint).toBeNull()
  })

  it('removes an invalid local setup draft', () => {
    const removeItem = vi.fn()
    const storage = { getItem: vi.fn(() => '{not-json'), removeItem }

    expect(readSetupDraft(storage, 'draft')).toBeNull()
    expect(removeItem).toHaveBeenCalledWith('draft')
  })

  it('returns a validated local setup draft', () => {
    const storage = { getItem: vi.fn(() => serializeSetupDraft(settings.profiles)), removeItem: vi.fn() }

    expect(readSetupDraft(storage, 'draft')).toEqual(settings.profiles)
    expect(storage.removeItem).not.toHaveBeenCalled()
  })
})

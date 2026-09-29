import { describe, expect, it, vi } from 'vitest'
import { ContractError, parseAppInstance, parseCompanionInfo, parseDataRecoveryView, parseDeviceAccessExpiryResult, parseInviteResult, parseMinecraftBrowseResult, parsePortDiagnostics, parseRecentServerSessions, parseServerLogResult, parseSettings, parseSnapshot, parseSupportReportExport, type Settings } from './contracts'
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

  it('accepts the exact not-paired Friend snapshot with unavailable Host capabilities', () => {
    const snapshot = parseSnapshot({
      mode: 'Friend',
      state: 'Not connected',
      detail: 'Paste the server code from the Host PC.',
      endpoint: '',
      lastConnectedUtc: null,
      remoteControlsEnabled: false,
      canStart: false,
      canStop: false,
      profiles: [],
      connectionId: '00000000-0000-0000-0000-000000000000',
      connections: [],
      connectionCode: null,
      hostVersion: null,
      friendVersion: '',
      hostProtocolVersion: null,
      protocolCompatible: true,
      hostCapabilities: null,
      credentialExpiresUtc: null,
      certificateExpiresUtc: null,
      expiryWarning: null,
      routeMode: 'DirectInternet',
      routeAddress: null,
      hostId: '00000000-0000-0000-0000-000000000000',
      connectionName: null,
      activity: null
    })

    expect(snapshot.mode).toBe('Friend')
    if (snapshot.mode === 'Friend') expect(snapshot.hostCapabilities).toEqual([])
  })

  it('normalizes unavailable capabilities recursively without accepting malformed lists', () => {
    const savedConnection = {
      mode: 'Friend',
      state: 'Disconnected/Unknown',
      detail: 'Waiting for a verified Host response.',
      endpoint: 'https://192.0.2.10:5131',
      lastConnectedUtc: null,
      remoteControlsEnabled: false,
      canStart: false,
      canStop: false,
      profiles: [],
      connectionId: '11111111-1111-4111-8111-111111111111',
      connections: null,
      hostCapabilities: null
    }
    const aggregate = {
      ...savedConnection,
      state: 'Not connected',
      detail: 'Paste the server code from the Host PC.',
      endpoint: '',
      connectionId: '00000000-0000-0000-0000-000000000000',
      connections: [savedConnection],
      hostCapabilities: undefined
    }

    const snapshot = parseSnapshot(aggregate)
    expect(snapshot.mode).toBe('Friend')
    if (snapshot.mode === 'Friend') {
      expect(snapshot.hostCapabilities).toEqual([])
      expect(snapshot.connections?.[0].hostCapabilities).toEqual([])
    }
    expect(() => parseSnapshot({ ...aggregate, hostCapabilities: 'server-logs-v1' })).toThrow(/must be a list/)
    expect(() => parseSnapshot({ ...aggregate, connections: [{ ...savedConnection, hostCapabilities: ['server-logs-v1', 1] }] }))
      .toThrow(/must be text/)
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

  it('accepts the normal idle Friend listener state', () => {
    const companion = parseCompanionInfo({
      listenerActive: false,
      listenerState: 'Idle',
      listenerWarning: null,
      endpoint: 'https://1.2.3.4:5131',
      fingerprint: null,
      certificates: null,
      route: { mode: 'DirectInternet', address: '' },
      devices: [],
      stopSafety: {}
    })

    expect(companion.listenerState).toBe('Idle')
    expect(companion.listenerWarning).toBeNull()
  })

  it('strictly decodes owner access state separately from credential state', () => {
    const companion = parseCompanionInfo({
      listenerActive: true,
      listenerState: 'Listening',
      listenerWarning: null,
      endpoint: 'https://1.2.3.4:5131',
      fingerprint: null,
      certificates: null,
      route: { mode: 'DirectInternet', address: '' },
      devices: [{
        id: 'device', profileId: 'profile', assignedProfileIds: ['profile'], name: 'Friend PC',
        canStart: true, canStop: false, canExtendTimer: false, canViewLogs: false,
        revoked: false, paired: true, approvalPending: false,
        credentialExpiresUtc: '2026-12-01T12:00:00Z', lastHeartbeatUtc: null,
        serverPermissions: [], accessExpiresUtc: '2026-10-01T12:00:00+00:00', accessExpired: false
      }],
      stopSafety: {}
    })
    const result = parseDeviceAccessExpiryResult({
      ok: true, code: 'AccessExpirySet', message: 'Saved.',
      accessExpiresUtc: '2026-10-01T12:00:00Z', accessExpired: false
    })

    expect(companion.devices[0].accessExpiresUtc).toBe('2026-10-01T12:00:00+00:00')
    expect(result.accessExpired).toBe(false)
    expect(() => parseCompanionInfo({ ...companion, devices: [{ ...companion.devices[0], accessExpired: 'no' }] })).toThrow(ContractError)
    expect(() => parseDeviceAccessExpiryResult({ ...result, accessExpiresUtc: '2026-10-01T14:00:00+02:00' })).toThrow(ContractError)
    expect(() => parseDeviceAccessExpiryResult({ ...result, accessExpiresUtc: null, accessExpired: true })).toThrow(ContractError)
  })

  it('accepts normal nullable fields from invite and browse responses', () => {
    const invite = parseInviteResult({
      ok: true,
      code: 'InviteReady',
      message: 'Server code ready.',
      password: 'TS3-test',
      expiresUtc: '2026-09-25T23:00:00Z',
      listenerActive: true,
      listenerWarning: null
    })
    const canceledBrowse = parseMinecraftBrowseResult({
      ok: false,
      code: 'Canceled',
      message: 'No path selected.',
      path: null
    })

    expect(invite.listenerWarning).toBeNull()
    expect(() => parseInviteResult({ ok: true, code: 'InviteReady', message: 'Server code ready.' })).toThrow(ContractError)
    expect(canceledBrowse.path).toBeNull()
  })

  it('strictly decodes bounded server-log records and states', () => {
    const logs = parseServerLogResult({
      ok: true,
      code: 'LogAvailable',
      message: 'Showing records from the exact active managed run.',
      sourceState: 'Active',
      runId: 'a'.repeat(32),
      records: [{ timestampUtc: '2026-09-28T12:00:00Z', severity: 'Warning', category: 'Lifecycle',
        stream: 'Server', message: 'Server started.' }],
      cursor: 'cursor',
      hasMore: false
    })

    expect(logs.records[0].severity).toBe('Warning')
    expect(() => parseServerLogResult({ ...logs, sourceState: 'Ready' })).toThrow(ContractError)
    expect(() => parseServerLogResult({ ...logs, records: [{ ...logs.records[0], message: 'x'.repeat(2049) }] })).toThrow(ContractError)
    expect(() => parseServerLogResult({ ...logs, records: [{ ...logs.records[0], timestampUtc: 'x'.repeat(41) }] })).toThrow(ContractError)
    expect(() => parseServerLogResult({ ...logs, records: Array.from({ length: 201 }, () => null) }))
      .toThrow(/too many entries/)
  })

  it('requires support-export byte metadata to match its UTF-8 content', () => {
    const content = '{"snowman":"☃"}\n'
    const report = {
      fileName: 'TogetherServer-support-report.json',
      contentType: 'application/json; charset=utf-8',
      content,
      sizeBytes: new TextEncoder().encode(content).byteLength
    }

    expect(parseSupportReportExport(report).sizeBytes).toBeGreaterThan(content.length)
    expect(() => parseSupportReportExport({ ...report, sizeBytes: content.length }))
      .toThrow(/does not match its UTF-8 content/)
  })

  it('strictly decodes bounded exact-run session summaries and honest legacy gaps', () => {
    const profileId = '11111111-1111-4111-8111-111111111111'
    const complete = {
      profileId,
      operationId: '22222222-2222-4222-8222-222222222222',
      gameKind: 'Valheim',
      startedUtc: '2026-09-28T12:00:00Z',
      endedUtc: '2026-09-28T13:01:30Z',
      durationSeconds: 3690,
      readyEverObserved: true,
      endReason: 'GracefulStop',
      outcome: 'GracefulStop',
      crashRecoveryScheduled: false,
      lastTrustedOnlinePlayers: 0,
      maximumTrustedOnlinePlayers: 4,
      backupResult: 'Completed'
    }
    const legacy = {
      ...complete,
      operationId: '33333333-3333-4333-8333-333333333333',
      startedUtc: null,
      endedUtc: null,
      durationSeconds: null,
      readyEverObserved: null,
      endReason: null,
      outcome: null,
      crashRecoveryScheduled: null,
      lastTrustedOnlinePlayers: null,
      maximumTrustedOnlinePlayers: null,
      backupResult: null
    }
    const parsed = parseRecentServerSessions({
      ok: true, code: 'RecentSessions', message: 'Showing 2 recent archived sessions.',
      profileId, sessions: [complete, legacy]
    })

    expect(parsed.sessions[0].maximumTrustedOnlinePlayers).toBe(4)
    expect(parsed.sessions[1].outcome).toBeNull()
    expect(parsed.sessions[1].endedUtc).toBeNull()
    expect(() => parseRecentServerSessions({ ...parsed, sessions: [{ ...complete, outcome: 'Succeeded' }] }))
      .toThrow(ContractError)
    expect(() => parseRecentServerSessions({ ...parsed, sessions: [{ ...complete, endReason: 'ProcessExited' }] }))
      .toThrow(/incomplete summary contract/)
    expect(() => parseRecentServerSessions({ ...parsed, sessions: [{ ...complete, durationSeconds: 3688 }] }))
      .toThrow(/incomplete summary contract/)
    expect(() => parseRecentServerSessions({ ...parsed, sessions: [{ ...complete, durationSeconds: null }] }))
      .toThrow(/incomplete summary contract/)
    expect(() => parseRecentServerSessions({ ...parsed, sessions: [{ ...legacy, endedUtc: complete.endedUtc }] }))
      .toThrow(/incomplete summary contract/)
    expect(() => parseRecentServerSessions({ ...parsed, sessions: [{ ...complete, lastTrustedOnlinePlayers: 5, maximumTrustedOnlinePlayers: 4 }] }))
      .toThrow(/inconsistent trusted player observations/)
    expect(() => parseRecentServerSessions({ ...parsed, sessions: Array.from({ length: 21 }, () => complete) }))
      .toThrow(/outside its supported bounds/)
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

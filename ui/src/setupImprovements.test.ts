import { describe, expect, it } from 'vitest'
import type { Profile } from './GameProfile'
import { setupIssueTarget, getSetupIssues } from './features/setup/HostSetupDialog'
import { parseProtectedSetupDraft, serializeProtectedSetupDraft } from './setupDraft'
import { setupPlannedPorts, setupPortSuggestion, setupProfileChanges, setupSupport, setupTemplate } from './setupImprovements'

const source: Profile = { id: '11111111-1111-4111-8111-111111111111', kind: 'Valheim', name: 'Friends', serverName: 'Friends',
  crossplay: false, publicListing: false, worldId: 'old-world', worldSource: 'Existing', worldDirectory: 'C:\\synthetic\\original',
  executablePath: 'C:\\synthetic\\server\\valheim_server.exe', gamePort: 2456, sharedSavesEnabled: true, worldLoadRehearsalId: 'old-rehearsal',
  crashRecovery: { enabled: true }, backups: { enabled: true, retentionCount: 5, minimumFreeSpaceMb: 1024 }, maintenance: { enabled: true, message: 'old' } }
const newId = '22222222-2222-4222-8222-222222222222'

describe('setup improvements', () => {
  it('reuses only nonsecret setup and creates distinct world, process and access scope', () => {
    const result = setupTemplate(source, newId, 'C:\\synthetic\\managed', [source])
    expect(result.id).toBe(newId)
    expect(result.worldId).toBe('')
    expect(result.worldSource).toBe('New')
    expect(result.worldDirectory).toBe(`C:\\synthetic\\managed\\${newId.replaceAll('-', '')}`)
    expect(result.gamePort).toBe(2458)
    expect(result.sharedSavesEnabled).toBe(false)
    expect(result.worldLoadRehearsalId).toBeNull()
    expect(result.crashRecovery?.enabled).toBe(false)
    expect(result.maintenance?.enabled).toBe(false)
    const minecraft: Profile = { ...source, kind: 'MinecraftJava', gamePort: 25565, minecraft: { serverJarPath: 'C:\\synthetic\\original\\server.jar' } }
    const next = setupTemplate(minecraft, newId, 'C:\\synthetic\\managed', [minecraft])
    expect(next.worldDirectory).toBe('')
    expect(next.minecraft?.serverJarPath).toBe('')
    expect(next.gamePort).toBe(25566)
    const custom = setupTemplate({ ...source, kind: 'Custom', custom: { gameName: 'Owner game', primaryProtocol: 'UDP', shareJoinAddress: true,
      additionalPorts: [{ protocol: 'TCP', port: 8080, family: 'IPv4', label: 'Old query' }] } }, newId, 'C:\\synthetic\\managed', [source])
    expect(custom.executablePath).toBe('')
    expect(custom.custom?.additionalPorts).toEqual([])
    expect(custom.worldDirectory).toBe('')
  })
  it('suggests ports for both Factorio game and local RCON and respects transport families', () => {
    const factorio: Profile = { ...source, kind: 'Factorio', gamePort: 34197, factorio: { rconPort: 27015 } }
    const candidate = { ...factorio, id: newId }
    const result = setupPortSuggestion(candidate, [factorio, candidate])
    expect(result.patch).toEqual({ gamePort: 34198, factorio: { rconPort: 27016 } })
    expect(setupPlannedPorts({ ...candidate, ...result.patch }).some(port => setupPlannedPorts(factorio).some(other => other.port === port.port && other.protocol === port.protocol))).toBe(false)
    const java: Profile = { ...source, kind: 'MinecraftJava', gamePort: 2456 }
    expect(setupPortSuggestion({ ...source, id: newId }, [java]).conflicts).toEqual([])
    const maximum: Profile = { ...source, kind: 'MinecraftJava', gamePort: 65535 }
    expect(setupPortSuggestion({ ...maximum, id: newId }, [maximum]).patch?.gamePort).toBe(1024)
  })
  it('does not suggest a primary-port change that leaves fixed custom secondary conflicts unresolved', () => {
    const custom: Profile = { ...source, kind: 'Custom', id: newId, custom: { gameName: 'Owner game', primaryProtocol: 'TCP', shareJoinAddress: true,
      additionalPorts: [{ protocol: 'UDP', port: 2456, family: 'Any', label: 'Fixed query' }] } }
    expect(setupPortSuggestion(custom, [source]).patch).toBeNull()
  })
  it('projects one setup draft, remembers its step and omits extra secrets, scripts and Host policy', () => {
    const withExtra = { ...source, password: 'never persist this', scripts: { start: 'raw script' }, remoteControlsEnabled: true } as Profile
    const text = serializeProtectedSetupDraft([withExtra, { ...source, id: newId }], 'server', source.id)
    expect(text).not.toContain('never persist this')
    expect(text).not.toContain('raw script')
    expect(text).not.toContain('remoteControlsEnabled')
    expect(parseProtectedSetupDraft(text)).toMatchObject({ activeProfileId: source.id, step: 'server', profiles: [{ id: source.id }] })
    expect(parseProtectedSetupDraft(text).profiles).toHaveLength(1)
    expect(() => parseProtectedSetupDraft(text.replace('"step":"server"', '"step":"arbitrary"'))).toThrow()
  })
  it('lists every known setup issue and maps it to a field or reviewed field group', () => {
    const broken: Profile = { ...source, serverName: '', worldId: '', worldDirectory: '', executablePath: '', gamePort: 0, worldSource: 'New' }
    const issues = getSetupIssues(broken, false, '', undefined, false)
    expect(issues).toHaveLength(6)
    expect(issues.map(message => setupIssueTarget(message, broken).field)).toEqual(['game-port', 'world-id', 'world-id', 'world-directory', 'server-selection', 'game-password'])
    expect(setupIssueTarget('Enter the world name from server.properties.', { ...source, kind: 'MinecraftJava' }).step).toBe('world')
    expect(setupSupport('Terraria').limits).toContain('Unknown')
    expect(setupSupport('MinecraftJava').limits).toContain('Paper')
  })
  it('summarizes setup changes without putting passwords or scripts into the review', () => {
    expect(setupProfileChanges(source, { ...source, gamePort: 2458, worldDirectory: 'C:\\synthetic\\new' }).map(change => change.label)).toEqual(['Save location', 'Game ports'])
  })
})

import { expect, it } from 'vitest'
import { accessListIssue, parseGameAccessList, parseGameSettings, parseGameSettingsChange, parseGameSettingsPreview } from './gameSettings'

const sha = 'A'.repeat(64)
const settings = { difficulty: 'normal', maximumPlayers: 20, gameMode: 'survival', allowListEnabled: false, forceGameMode: false }
const view = { ok: true, code: 'GameSettingsReady', message: 'Next Start', kind: 'MinecraftJava',
  sha256: sha, settings, canUndo: false, lists: [{ key: 'allow-list', label: 'Allowed players', available: true }] }
const javaEntry = { identity: '11111111-1111-4111-8111-111111111111', name: 'TestPlayer', ignoresPlayerLimit: null }
const list = { ok: true, code: 'AccessListReady', message: 'Next Start', kind: 'MinecraftJava', key: 'allow-list',
  label: 'Allowed players', sha256: sha, entries: [javaEntry], canUndo: false }
const preview = { ok: true, code: 'SettingsPreviewReady', message: 'Review', key: 'server-properties',
  expectedSha256: sha, proposedSha256: 'B'.repeat(64),
  changes: [{ key: 'difficulty', label: 'Difficulty', before: 'difficulty=normal', after: 'difficulty=hard' }] }

it('decodes reviewed settings and rejects extra, missing, mistyped and invalid edition fields', () => {
  expect(parseGameSettings(view).settings).toEqual(settings)
  for (const bad of [
    { ...view, rawPath: 'caller-input' }, { ...view, settings: { ...settings, worldId: 'other' } },
    { ...view, settings: { ...settings, forceGameMode: undefined } },
    { ...view, settings: { ...settings, allowListEnabled: 'true' } },
    { ...view, settings: { ...settings, difficulty: 'nightmare' } },
    { ...view, settings: { ...settings, maximumPlayers: 1.2 } },
    { ...view, settings: { ...settings, maximumPlayers: 201 } },
    { ...view, settings: { ...settings, maximumPlayers: 0 } },
    { ...view, settings: { ...settings, maximumPlayers: NaN } },
    { ...view, kind: 'MinecraftBedrock', settings: { ...settings, gameMode: 'spectator' } },
    { ...view, sha256: 'bad' }, { ...view, sha256: null },
    { ...view, lists: [...view.lists, ...view.lists] },
    { ...view, lists: [{ key: 'permissions', label: 'Permissions', available: true }] },
    { ...view, kind: 'Valheim' }, { ...view, settings: null }
  ]) expect(() => parseGameSettings(bad)).toThrow()
})

it('keeps unavailable properties and supported access lists as a typed error without invented settings', () => {
  expect(parseGameSettings({ ...view, ok: false, code: 'SettingsNeedFileReview', settings: null,
    sha256: null, canUndo: false }).lists).toHaveLength(1)
  expect(parseGameSettings({ ...view, kind: 'Valheim', settings: null, sha256: null,
    lists: [{ key: 'permit-list', label: 'Permitted players', available: false }] }).settings).toBeNull()
  expect(() => parseGameSettings({ ...view, ok: false })).toThrow()
  expect(() => parseGameSettings({ ...view, settings: null, sha256: null, canUndo: true })).toThrow()
})

it('accepts only bounded exact preview changes with consistent hashes', () => {
  expect(parseGameSettingsPreview(preview).changes[0].after).toBe('difficulty=hard')
  for (const bad of [
    { ...preview, content: 'raw file' }, { ...preview, expectedSha256: null },
    { ...preview, proposedSha256: 'invalid' }, { ...preview, key: 'not-a-file' },
    { ...preview, changes: [{ ...preview.changes[0], key: 'server-port' }] },
    { ...preview, changes: [...preview.changes, ...preview.changes] },
    { ...preview, changes: [{ ...preview.changes[0], before: null, after: null }] },
    { ...preview, changes: [{ ...preview.changes[0], after: 'difficulty=hard\nserver-port=1234' }] },
    { ...preview, changes: [{ ...preview.changes[0], before: 'x'.repeat(513) }] },
    { ...preview, ok: false }
  ]) expect(() => parseGameSettingsPreview(bad)).toThrow()
})

it('validates Java UUID/name identity, per-game list keys, nulls and entry count', () => {
  expect(parseGameAccessList(list).entries).toEqual([javaEntry])
  for (const bad of [
    { ...list, entries: [{ ...javaEntry, name: 'name with spaces' }] },
    { ...list, entries: [{ ...javaEntry, identity: '00000000-0000-0000-0000-000000000000' }] },
    { ...list, entries: [{ ...javaEntry, ignoresPlayerLimit: true }] },
    { ...list, entries: [{ ...javaEntry, name: 'name', admin: true }] },
    { ...list, entries: [{ ...javaEntry, name: null }] },
    { ...list, entries: [javaEntry, { ...javaEntry, name: 'Another' }] },
    { ...list, entries: [javaEntry, { ...javaEntry, identity: '22222222-2222-4222-8222-222222222222', name: 'testplayer' }] },
    { ...list, entries: Array.from({ length: 129 }, () => javaEntry) },
    { ...list, key: 'admin-list' }, { ...list, sha256: null }, { ...list, ok: false }
  ]) expect(() => parseGameAccessList(bad)).toThrow()
})

it('handles Bedrock names, optional numeric XUIDs and explicit player-limit overrides', () => {
  const entry = { identity: null, name: 'Test Gamer', ignoresPlayerLimit: false }
  expect(parseGameAccessList({ ...list, kind: 'MinecraftBedrock', entries: [entry] }).entries[0].identity).toBeNull()
  for (const bad of [{ ...entry, identity: '' }, { ...entry, identity: 'a123' }, { ...entry, name: 'Player\nName' },
    { ...entry, name: 'Player\u202eName' }, { ...entry, name: ' Player ' }, { ...entry, ignoresPlayerLimit: null }])
    expect(() => parseGameAccessList({ ...list, kind: 'MinecraftBedrock', entries: [bad] })).toThrow()
})

it('keeps Valheim platform IDs case-sensitive and rejects names and commands', () => {
  const entry = { identity: 'Steam_111', name: null, ignoresPlayerLimit: null }
  expect(parseGameAccessList({ ...list, kind: 'Valheim', key: 'permit-list', entries: [entry] }).entries).toEqual([entry])
  expect(accessListIssue('Valheim', [entry, { ...entry, identity: 'steam_111' }])).toBeNull()
  for (const bad of [{ ...entry, identity: 'Steam_111\nban Other' }, { ...entry, identity: '111' },
    { ...entry, name: 'SomePlayer' }, { ...entry, ignoresPlayerLimit: true }])
    expect(() => parseGameAccessList({ ...list, kind: 'Valheim', key: 'permit-list', entries: [bad] })).toThrow()
})

it('requires canonical saved results before enabling Undo', () => {
  const result = { ok: true, code: 'FileSaved', message: 'Saved', key: 'server-properties', sha256: sha, canUndo: true }
  expect(parseGameSettingsChange(result).canUndo).toBe(true)
  expect(() => parseGameSettingsChange({ ...result, sha256: null })).toThrow()
  expect(() => parseGameSettingsChange({ ...result, canUndo: 'true' })).toThrow()
  expect(() => parseGameSettingsChange({ ...result, command: 'arbitrary' })).toThrow()
  expect(() => parseGameSettingsChange({ ...result, ok: false })).toThrow()
})

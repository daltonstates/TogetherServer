import { describe, expect, it } from 'vitest'
import { editorBulkPlayers, editorFileDiff } from './editorChanges'

describe('raw file comparison', () => {
  it('locates changed lines and retains unchanged context and CRLF differences exactly', () => {
    const result = editorFileDiff('same\r\nport=25565\r\nmode=normal\r\nend\r\n', 'same\r\nport=25566\r\nmode=normal\r\nnew=true\r\nend\r\n')
    expect(result.unchangedStart).toBe(1)
    expect(result.unchangedEnd).toBe(2)
    expect(result.lines).toEqual([
      { type: 'removed', before: 2, after: null, text: 'port=25565\r' },
      { type: 'added', before: null, after: 2, text: 'port=25566\r' },
      { type: 'unchanged', before: 3, after: 3, text: 'mode=normal\r' },
      { type: 'added', before: null, after: 4, text: 'new=true\r' }
    ])
    expect(editorFileDiff('a\r\n', 'a\n').lines[0].text).toBe('a\r')
  })
  it('bounds a large comparison without silently losing changed-line counts', () => {
    const result = editorFileDiff(Array(600).fill('old').join('\n'), Array(600).fill('new').join('\n'), 12)
    expect(result.lines).toHaveLength(12)
    expect(result.omitted).toBe(1188)
  })
})

describe('reviewed bulk player paste', () => {
  it('requires exact game-specific identities, detects duplicates against saved entries, and adds no limit bypass', () => {
    expect(editorBulkPlayers('Valheim', 'Steam_111\nSteam_222', []).entries.map(entry => entry.identity)).toEqual(['Steam_111', 'Steam_222'])
    expect(editorBulkPlayers('Valheim', 'Steam_111', [{ identity: 'Steam_111', name: null, ignoresPlayerLimit: null }]).issue).toMatch(/repeated/)
    expect(editorBulkPlayers('MinecraftJava', 'Alice\t11111111-1111-4111-8111-111111111111', []).entries).toEqual([
      { name: 'Alice', identity: '11111111-1111-4111-8111-111111111111', ignoresPlayerLimit: null }
    ])
    expect(editorBulkPlayers('MinecraftJava', 'Alice', []).issue).toMatch(/Line 1/)
    expect(editorBulkPlayers('MinecraftBedrock', 'Test Gamer\t123456', []).entries[0]).toEqual({ name: 'Test Gamer', identity: '123456', ignoresPlayerLimit: false })
    expect(editorBulkPlayers('MinecraftBedrock', 'Test Gamer\ntest gamer', []).issue).toBe('Remove the repeated player name.')
  })
  it('rejects oversize, malformed and unsupported input before any mutation', () => {
    expect(editorBulkPlayers('Valheim', 'x'.repeat(24_001), []).entries).toEqual([])
    expect(editorBulkPlayers('Valheim', Array(129).fill('Steam_111').join('\n'), []).issue).toMatch(/128/)
    expect(editorBulkPlayers('Custom', 'Alice', []).issue).not.toBeNull()
    expect(editorBulkPlayers('MinecraftJava', 'Alice\t00000000-0000-0000-0000-000000000000', []).issue).toMatch(/valid/)
  })
})

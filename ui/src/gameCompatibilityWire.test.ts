import { describe, expect, it } from 'vitest'
import { parseGameCompatibility, parseGameRequirements } from './gameCompatibilityWire'

const profileId = '11111111-1111-4111-8111-111111111111'
const requirements = { profileId, kind: 'Factorio', gameName: 'Factorio', requiredVersion: '2.0.42',
  versionSource: 'OwnerReported', addOnState: 'Known', addOns: [{ id: 'reviewed', name: 'reviewed', version: '1.2.3', type: 'FactorioMod' }],
  checkedUtc: '2026-10-08T15:00:00Z', guidance: 'Use the same game and enabled mods.' }
const result = { ok: true, code: 'CompatibilityRead', message: 'Informational.', requirements,
  clientVersion: '2.0.41', clientVersionSource: 'Manual', versionComparison: 'Mismatch', addOnComparison: 'Unknown' }

describe('pre-join compatibility wire boundary', () => {
  it('keeps owner-reported, manual, mismatch and unknown distinct', () => {
    expect(parseGameCompatibility(result)).toMatchObject({ versionComparison: 'Mismatch', addOnComparison: 'Unknown', clientVersionSource: 'Manual', requirements: { versionSource: 'OwnerReported' } })
    expect(parseGameCompatibility({ ...result, clientVersion: null, clientVersionSource: 'Unknown', versionComparison: 'Unknown' }).versionComparison).toBe('Unknown')
  })
  it('rejects an invented match, wrong source, path, oversized inventory or control text', () => {
    expect(() => parseGameCompatibility({ ...result, versionComparison: 'Match' })).toThrow()
    expect(() => parseGameCompatibility({ ...result, clientVersion: null })).toThrow()
    for (const patch of [{ requiredVersion: 'C:\\secret' }, { addOns: new Array(65).fill(requirements.addOns[0]) },
      { addOns: [{ ...requirements.addOns[0], name: 'C:\\private' }] }, { guidance: 'safe\u202eunsafe' },
      { versionSource: 'Unknown' }, { addOnState: 'Unknown' }])
      expect(() => parseGameRequirements({ ...result, requirements: { ...requirements, ...patch } })).toThrow()
  })
  it('accepts honest old-peer failure without requirement data', () => {
    expect(parseGameRequirements({ ok: false, code: 'RequirementsUpdateRequired', message: 'Update the Host.', requirements: null }).requirements).toBeNull()
  })
})

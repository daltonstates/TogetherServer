import { describe, expect, it, vi } from 'vitest'
import { defaultUiPreferences, readUiPreferences, writeUiPreferences } from './qolPreferences'

const id = '11111111-1111-4111-8111-111111111111'
describe('optional workspace preferences', () => {
  it('keeps server identity and view choices while dropping private runtime extras', () => {
    const setItem = vi.fn()
    writeUiPreferences({ setItem }, Object.assign({ ...defaultUiPreferences, theme: 'system' as const,
      favoriteServers: [id, id, 'C:\\Private\\world'], serverTabs: { [id]: 'backups', PRIVATE_ENDPOINT: 'logs' } },
    { password: 'PRIVATE_SECRET', worldDirectory: 'PRIVATE_WORLD', updateSnooze: { version: '0.3.0', until: 123 } }))
    const saved = setItem.mock.calls[0][1]
    expect(saved).not.toContain('PRIVATE_')
    expect(saved).not.toContain('updateSnooze')
    expect(readUiPreferences({ getItem: () => saved })).toMatchObject({ theme: 'system', favoriteServers: [id], serverTabs: { [id]: 'backups' } })
  })

  it('recovers from corrupt, oversized or unavailable optional storage', () => {
    for (const value of ['{bad', '[]', 'x'.repeat(100_001)])
      expect(readUiPreferences({ getItem: () => value })).toEqual(defaultUiPreferences)
    expect(readUiPreferences({ getItem: () => { throw new Error('Unavailable') } })).toEqual(defaultUiPreferences)
    expect(() => writeUiPreferences({ setItem: () => { throw new Error('Full') } }, defaultUiPreferences)).not.toThrow()
  })
})

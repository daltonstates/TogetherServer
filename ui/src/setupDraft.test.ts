import { describe, expect, it, vi } from 'vitest'
import type { ActionResult, Settings } from './contracts'
import {
  customPortKey,
  hasSensitiveSetupDraft,
  readSetupDraft,
  readSetupDraftFrom,
  reconcileProfileRemoval,
  removeSetupDraft,
  removeSetupDraftFrom,
  safeFileName,
  serializeSetupDraft,
  writeSetupDraftTo
} from './setupDraft'

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

describe('setup draft helpers', () => {
  it('detects only unsaved scripts and entered passwords as sensitive', () => {
    const scripts = { profile: { start: 'start', status: 'status', stop: 'stop' } }

    expect(hasSensitiveSetupDraft({}, scripts, { profile: true })).toBe(false)
    expect(hasSensitiveSetupDraft({}, scripts, { profile: false })).toBe(true)
    expect(hasSensitiveSetupDraft({ profile: 'secret' }, {}, {})).toBe(true)
  })

  it('uses a port-row key that does not depend on mutable port values', () => {
    expect(customPortKey('profile-id', 2)).toBe('profile-id-additional-port-2')
  })

  it('persists profiles only, never Host policy fields', () => {
    const serialized = serializeSetupDraft([])

    expect(serialized).toBe('{"version":2,"profiles":[]}')
    expect(serialized).not.toContain('remoteControlsEnabled')
    expect(serialized).not.toContain('companionEndpoint')
  })

  it('never exposes directory segments in recovery filenames', () => {
    expect(safeFileName('C:\\private\\runs.invalid.json')).toBe('runs.invalid.json')
    expect(safeFileName('/private/runs.invalid.json')).toBe('runs.invalid.json')
  })

  it('treats unavailable browser storage as best-effort cleanup', () => {
    const storage = { getItem: () => '{bad json', removeItem: () => { throw new DOMException('denied', 'SecurityError') } }

    expect(() => removeSetupDraft(storage, 'draft')).not.toThrow()
    expect(readSetupDraft(storage, 'draft')).toBeNull()
  })

  it('also contains failures thrown while acquiring browser storage', () => {
    const denied = () => { throw new DOMException('denied', 'SecurityError') }

    expect(readSetupDraftFrom(denied, 'draft')).toBeNull()
    expect(() => writeSetupDraftTo(denied, 'draft', [])).not.toThrow()
    expect(() => removeSetupDraftFrom(denied, 'draft')).not.toThrow()
  })

  it('keeps the unsaved draft when profile removal is rejected', () => {
    const authoritative = { ...settings, idleMinutes: 30 }
    const failed = { ok: false, snapshot: { settings: authoritative } as ActionResult['snapshot'] }
    const saved = { ok: true, snapshot: { settings: authoritative } as ActionResult['snapshot'] }

    expect(reconcileProfileRemoval(settings, failed)).toEqual({ committed: false, settings })
    expect(reconcileProfileRemoval(settings, saved)).toEqual({ committed: true, settings: authoritative })
  })

  it('writes the versioned profiles-only payload through an acquired store', () => {
    const setItem = vi.fn()

    writeSetupDraftTo(() => ({ setItem }), 'draft', [])

    expect(setItem).toHaveBeenCalledWith('draft', '{"version":2,"profiles":[]}')
  })
})

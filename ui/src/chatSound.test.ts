import { describe, expect, it, vi } from 'vitest'
import { createChatSoundPlayer, createIncomingChatTracker, readChatSoundPreference, saveChatSoundPreference } from './chatSound'

const self = '00000000-0000-0000-0000-000000000000'
const friend = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
const now = Date.parse('2026-10-19T12:00:00Z')
const entry = (id: string, delta = 0, authorId = friend) => ({ id, authorId,
  sentUtc: new Date(now + delta).toISOString(), signature: 'verified-by-local-service' })
const room = (entries: ReturnType<typeof entry>[]) => ({ ok: true, code: 'ChatSynced', hostId: 'Host', selfAuthorId: self, entries })

describe('incoming chat sound tracking', () => {
  it('ignores initial history, own messages, duplicates, backfill and untrusted dates', () => {
    const tracker = createIncomingChatTracker(false)
    const first = entry('first', -1000)
    expect(tracker.observe(room([first]), now)).toEqual([])
    expect(tracker.observe(room([first, entry('recent-backfill', -500)]), now)).toEqual([])
    const incoming = entry('incoming')
    expect(tracker.observe(room([first, incoming]), now)).toEqual(['incoming'])
    expect(tracker.observe(room([first, incoming]), now)).toEqual([])
    const own = entry('own', 1, self)
    expect(tracker.observe(room([incoming, own]), now)).toEqual([])
    expect(tracker.observe(room([entry('backfill', -500), incoming, own]), now)).toEqual([])
    expect(tracker.observe(room([entry('far-future', 60_000), entry('invalid', 1, 'unknown-author')]), now)).toEqual([])
    const stale = createIncomingChatTracker(true)
    stale.observe(room([entry('earlier', -60_000)]), now)
    expect(stale.observe(room([entry('old-history', -20_000)]), now)).toEqual([])
  })

  it('baselines quietly after cached/offline/denied copies, visibility gaps and identity changes', () => {
    const tracker = createIncomingChatTracker(false)
    tracker.observe(room([entry('first', -1000)]), now)
    expect(tracker.observe({ ...room([entry('offline')]), code: 'ChatReady' }, now)).toEqual([])
    expect(tracker.observe(room([entry('reconnected', 1)]), now)).toEqual([])
    tracker.reset()
    expect(tracker.observe(room([entry('after-hidden', 2)]), now)).toEqual([])
    expect(tracker.observe({ ...room([entry('denied', 3)]), ok: false }, now)).toEqual([])
    expect(tracker.observe(room([entry('allowed-again', 4)]), now)).toEqual([])
    expect(tracker.observe({ ...room([entry('another-host', 5)]), hostId: 'Changed Host' }, now)).toEqual([])
    expect(tracker.observe({ ...room([entry('unknown-self', 6)]), selfAuthorId: null }, now)).toEqual([])
    expect(tracker.observe(room([entry('known-self', 7)]), now)).toEqual([])
  })

  it('keeps optional preferences local to the room and tolerates unavailable storage', () => {
    const values = new Map<string, string>()
    const storage = { getItem: (key: string) => values.get(key) ?? null, setItem: (key: string, value: string) => { values.set(key, value) } }
    expect(readChatSoundPreference('one', storage)).toBe(false)
    saveChatSoundPreference('one', true, storage)
    expect(readChatSoundPreference('one', storage)).toBe(true)
    expect(readChatSoundPreference('two', storage)).toBe(false)
    saveChatSoundPreference('one', false, storage)
    expect(readChatSoundPreference('one', storage)).toBe(false)
    const unavailable = { getItem: () => { throw new Error('Unavailable') }, setItem: () => { throw new Error('Unavailable') } }
    expect(readChatSoundPreference('one', unavailable)).toBe(false)
    expect(() => saveChatSoundPreference('one', true, unavailable)).not.toThrow()
  })
})

describe('optional Web Audio chat sound', () => {
  function fakeAudio() {
    const tone = { type: '', frequency: { setValueAtTime: vi.fn() }, connect: vi.fn(), disconnect: vi.fn(), start: vi.fn(), stop: vi.fn(), onended: null as (() => void) | null }
    const volume = { gain: { setValueAtTime: vi.fn(), exponentialRampToValueAtTime: vi.fn() }, connect: vi.fn(), disconnect: vi.fn() }
    const context = { state: 'suspended', currentTime: 2, destination: {},
      resume: vi.fn(async () => { context.state = 'running' }), close: vi.fn(async () => { context.state = 'closed' }),
      createOscillator: vi.fn(() => tone), createGain: vi.fn(() => volume) }
    return { context, tone, volume }
  }

  it('waits for a user interaction, plays one soft bounded tone and releases audio on mute', async () => {
    const { context, tone, volume } = fakeAudio()
    const factory = vi.fn(() => context as unknown as AudioContext)
    const player = createChatSoundPlayer(factory)
    player.play()
    expect(factory).not.toHaveBeenCalled()
    await player.unlock()
    expect(context.resume).toHaveBeenCalledOnce()
    player.play(); player.play()
    expect(context.createOscillator).toHaveBeenCalledOnce()
    expect(volume.gain.exponentialRampToValueAtTime).toHaveBeenCalledWith(.035, 2.012)
    expect(tone.stop).toHaveBeenCalledWith(2.2)
    tone.onended?.()
    expect(tone.disconnect).toHaveBeenCalledOnce()
    expect(volume.disconnect).toHaveBeenCalledOnce()
    player.dispose(); player.play()
    expect(context.close).toHaveBeenCalledOnce()
    expect(context.createOscillator).toHaveBeenCalledOnce()
  })

  it('handles missing audio, autoplay rejection and audio failures without chat errors', async () => {
    const missing = createChatSoundPlayer(() => null)
    await expect(missing.unlock()).resolves.toBeUndefined()
    expect(() => missing.play()).not.toThrow()
    const { context } = fakeAudio()
    context.resume.mockRejectedValue(new Error('Autoplay denied'))
    const blocked = createChatSoundPlayer(() => context as unknown as AudioContext)
    await expect(blocked.unlock()).resolves.toBeUndefined()
    blocked.play()
    expect(context.createOscillator).not.toHaveBeenCalled()
    context.state = 'running'
    context.createOscillator.mockImplementation(() => { throw new Error('Audio unavailable') })
    expect(() => blocked.play()).not.toThrow()
    expect(() => blocked.dispose()).not.toThrow()
  })
})

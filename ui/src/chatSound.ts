type SoundEntry = { id: string; authorId: string; sentUtc: string; signature: string }
type SoundRoom = {
  ok: boolean; code: string; hostId: string; entries: readonly SoundEntry[]
  selfAuthorId?: string | null
}
const hostAuthor = '00000000-0000-0000-0000-000000000000'
const authorIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export function chatAuthorId(room: SoundRoom, host: boolean): string | null {
  const value = room.selfAuthorId ?? (host ? hostAuthor : null)
  return value && authorIdPattern.test(value) ? value.toLowerCase() : null
}

/** Missing identity, reconnects and older copies establish a baseline quietly. */
export function createIncomingChatTracker(host: boolean) {
  let identity = '', newest = -Infinity, establishedAt = Infinity
  let seen = new Set<string>()
  let ready = false
  return {
    reset() { ready = false },
    observe(room: SoundRoom, nowMs: number): string[] {
      const self = chatAuthorId(room, host)
      const currentIdentity = `${room.hostId.toLowerCase()}:${self}`
      if (!room.ok || !self || !host && room.code !== 'ChatSynced' || !Number.isFinite(nowMs)) {
        ready = false
        return []
      }
      const continuing = ready && identity === currentIdentity
      const incoming = continuing ? room.entries.filter(entry => {
        const time = Date.parse(entry.sentUtc)
        return !seen.has(entry.id.toLowerCase()) && entry.authorId.toLowerCase() !== self &&
          authorIdPattern.test(entry.authorId) && !!entry.signature && Number.isFinite(time) &&
          time > newest && time >= establishedAt && time >= nowMs - 15_000 && time <= nowMs + 5_000
      }).map(entry => entry.id) : []
      if (!continuing) establishedAt = nowMs
      if (identity !== currentIdentity) newest = -Infinity
      identity = currentIdentity
      for (const entry of room.entries) {
        const time = Date.parse(entry.sentUtc)
        if (Number.isFinite(time)) newest = Math.max(newest, time)
      }
      seen = new Set(room.entries.map(entry => entry.id.toLowerCase()))
      ready = true
      return incoming
    }
  }
}

export function readChatSoundPreference(scope: string, storage?: Pick<Storage, 'getItem'>): boolean {
  try { return (storage ?? localStorage).getItem(`together-server.chat.sound.${scope}`) === 'on' }
  catch { return false }
}

export function saveChatSoundPreference(scope: string, enabled: boolean, storage?: Pick<Storage, 'setItem'>): void {
  try { (storage ?? localStorage).setItem(`together-server.chat.sound.${scope}`, enabled ? 'on' : 'off') }
  catch { /* Optional sound preferences never block chat. */ }
}

/** Web Audio is opened by a room interaction. Autoplay refusal stays quiet. */
export function createChatSoundPlayer(createContext = (): AudioContext | null =>
  typeof AudioContext === 'function' ? new AudioContext() : null) {
  let context: AudioContext | null = null
  let lastPlayed = -Infinity
  return {
    async unlock() {
      try {
        if (!context || context.state === 'closed') context = createContext()
        if (context?.state === 'suspended') await context.resume()
      } catch { /* This browser may require another explicit interaction. */ }
    },
    play() {
      if (!context || context.state !== 'running' || context.currentTime - lastPlayed < .4) return
      try {
        const time = context.currentTime
        const tone = context.createOscillator(), volume = context.createGain()
        tone.type = 'sine'
        tone.frequency.setValueAtTime(660, time)
        tone.frequency.setValueAtTime(880, time + .075)
        volume.gain.setValueAtTime(.0001, time)
        volume.gain.exponentialRampToValueAtTime(.035, time + .012)
        volume.gain.exponentialRampToValueAtTime(.0001, time + .18)
        tone.connect(volume); volume.connect(context.destination)
        tone.onended = () => { tone.disconnect(); volume.disconnect() }
        tone.start(time); tone.stop(time + .2)
        lastPlayed = time
      } catch { /* Sound is optional; delivery and chat feedback stay intact. */ }
    },
    dispose() {
      const previous = context
      context = null; lastPlayed = -Infinity
      if (previous && previous.state !== 'closed') {
        try { void previous.close().catch(() => undefined) } catch { /* Already closed. */ }
      }
    }
  }
}

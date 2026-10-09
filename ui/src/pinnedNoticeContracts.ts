import { ContractError, parseChatRoomView, type ChatRoomView, type Decoder } from './contracts'

export const maximumNoticeTextLength = 2000
export type PinnedServerNotice = {
  hostId: string; profileId: string; revision: number; updatedUtc: string; text: string | null; signature: string
}
export type PinnedNoticeFields = {
  notice: PinnedServerNotice | null; noticeCached: boolean; noticeSupported: boolean
}
export type ChatRoomWithNotice = ChatRoomView & PinnedNoticeFields

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const emptyGuid = '00000000-0000-0000-0000-000000000000'
const forbiddenText = /[\u202a-\u202e\u2066-\u2069]/u

export function validPinnedNoticeText(value: string): boolean {
  return value.length > 0 && value.length <= maximumNoticeTextLength &&
    value.trim().length > 0 && !forbiddenText.test(value) &&
    !Array.from(value).some(character => /\p{Cc}/u.test(character) && character !== '\n' && character !== '\t')
}

function object(value: unknown, context: string): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value))
    throw new ContractError(`${context} must be an object.`)
  return value as Record<string, unknown>
}

export function parsePinnedNoticeFields(value: unknown, expectedHostId: string, expectedProfileId: string,
  context = 'chat room'): PinnedNoticeFields {
  const source = object(value, context)
  for (const field of ['noticeCached', 'noticeSupported']) {
    if (source[field] !== undefined && typeof source[field] !== 'boolean')
      throw new ContractError(`${context}.${field} must be a boolean.`)
  }
  const noticeCached = source.noticeCached === true
  const noticeSupported = source.noticeSupported === true
  if (source.notice === undefined || source.notice === null)
    return { notice: null, noticeCached, noticeSupported }
  const notice = object(source.notice, `${context}.notice`)
  if (typeof notice.hostId !== 'string' || typeof notice.profileId !== 'string' ||
      !guid.test(notice.hostId) || !guid.test(notice.profileId) ||
      notice.hostId.toLowerCase() === emptyGuid || notice.profileId.toLowerCase() === emptyGuid ||
      notice.hostId.toLowerCase() !== expectedHostId.toLowerCase() ||
      notice.profileId.toLowerCase() !== expectedProfileId.toLowerCase())
    throw new ContractError(`${context}.notice must match this server room.`)
  if (typeof notice.revision !== 'number' || !Number.isSafeInteger(notice.revision) || notice.revision < 1)
    throw new ContractError(`${context}.notice.revision must be a positive safe integer.`)
  if (typeof notice.updatedUtc !== 'string' || notice.updatedUtc.length > 40 ||
      !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(notice.updatedUtc) ||
      !Number.isFinite(Date.parse(notice.updatedUtc)) || Date.parse(notice.updatedUtc) < 0 ||
      Date.parse(notice.updatedUtc) > Date.now() + 5 * 60_000)
    throw new ContractError(`${context}.notice.updatedUtc must be a valid UTC time.`)
  if (notice.text !== null && (typeof notice.text !== 'string' || !validPinnedNoticeText(notice.text)))
    throw new ContractError(`${context}.notice.text exceeded pinned notice limits.`)
  if (typeof notice.signature !== 'string' || !/^[A-Za-z0-9+/]{86}==$/.test(notice.signature))
    throw new ContractError(`${context}.notice.signature must be a bounded signed revision.`)
  if (!noticeCached && !noticeSupported)
    throw new ContractError(`${context}.notice must be supported or marked as a cached copy.`)
  return { notice: {
    hostId: notice.hostId, profileId: notice.profileId, revision: notice.revision,
    updatedUtc: notice.updatedUtc, text: notice.text, signature: notice.signature
  }, noticeCached, noticeSupported }
}

export const parseChatRoomWithNotice: Decoder<ChatRoomWithNotice> = (value, context = 'chat room') => {
  const room = parseChatRoomView(value, context)
  return { ...room, ...parsePinnedNoticeFields(value, room.hostId, room.profileId, context) }
}

export type PlannedHandoffStatus = {
  pending: boolean; code: string; message: string; successorDeviceId: string | null
  requestId: string | null; recipientState: 'Requested' | 'Accepted' | 'Declined' | 'Prepared' | 'Completed' | 'Canceled' | null
  preparedVersionHash: string | null
  finalVersion: number | null; receiptConfirmed: boolean; canComplete: boolean; canCancel: boolean
}

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
export function parsePlannedHandoffStatus(value: unknown): PlannedHandoffStatus {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Hosting request could not be read.')
  const item = value as Record<string, unknown>
  const requestId = item.requestId ?? null
  const recipientState = item.recipientState ?? null
  const successorDeviceId = item.successorDeviceId ?? null
  const finalVersion = item.finalVersion ?? null
  const preparedVersionHash = item.preparedVersionHash ?? null
  if (['pending', 'receiptConfirmed', 'canComplete', 'canCancel'].some(key => typeof item[key] !== 'boolean') ||
    typeof item.code !== 'string' || !item.code || item.code.length > 100 ||
    typeof item.message !== 'string' || !item.message || item.message.length > 1000 ||
    requestId !== null && (typeof requestId !== 'string' || !guidPattern.test(requestId)) ||
    successorDeviceId !== null && (typeof successorDeviceId !== 'string' || !guidPattern.test(successorDeviceId)) ||
    finalVersion !== null && (typeof finalVersion !== 'number' || !Number.isSafeInteger(finalVersion) || finalVersion < 1) ||
    recipientState !== null && !['Requested', 'Accepted', 'Declined', 'Prepared', 'Completed', 'Canceled'].includes(String(recipientState)) ||
    preparedVersionHash !== null && (typeof preparedVersionHash !== 'string' || !/^[0-9a-f]{64}$/i.test(preparedVersionHash)) ||
    (requestId === null) !== (recipientState === null) || requestId !== null && successorDeviceId === null)
    throw new Error('Hosting request is incomplete or invalid. Retry before continuing.')
  return { pending: item.pending as boolean, code: item.code, message: item.message,
    successorDeviceId: successorDeviceId as string | null, requestId: requestId as string | null,
    recipientState: recipientState as PlannedHandoffStatus['recipientState'], finalVersion: finalVersion as number | null,
    preparedVersionHash: preparedVersionHash as string | null,
    receiptConfirmed: item.receiptConfirmed as boolean, canComplete: item.canComplete as boolean, canCancel: item.canCancel as boolean }
}

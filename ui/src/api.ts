import { ContractError, type Decoder } from './contracts'

export const localHeaders = { 'Content-Type': 'application/json', 'X-TogetherServer-Local': '1' }

export class ApiError extends Error {
  readonly status: number
  readonly code: string

  constructor(status: number, code: string, message: string, options?: ErrorOptions) {
    super(message, options)
    this.name = 'ApiError'
    this.status = status
    this.code = code
  }
}

function errorFields(value: unknown): { code?: string; message?: string } {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return {}
  const source = value as Record<string, unknown>
  return {
    code: typeof source.code === 'string' ? source.code : undefined,
    message: typeof source.message === 'string' ? source.message :
      typeof source.detail === 'string' ? source.detail : undefined
  }
}

async function readPayload(response: Response): Promise<unknown> {
  const body = await response.text()
  if (!body) return null
  try { return JSON.parse(body) as unknown }
  catch (error) {
    throw new ApiError(response.status, 'InvalidJson', 'The local app returned malformed JSON.', { cause: error })
  }
}

export async function requestJson<T>(path: string, decoder: Decoder<T>, init: RequestInit = {}): Promise<T> {
  let response: Response
  try { response = await fetch(path, init) }
  catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw new ApiError(0, 'LocalAppUnavailable', 'Could not reach the local TogetherServer service.', { cause: error })
  }

  const payload = await readPayload(response)
  if (!response.ok) {
    const details = errorFields(payload)
    throw new ApiError(response.status, details.code ?? `Http${response.status}`,
      details.message ?? `The local app returned ${response.status}.`)
  }

  try { return decoder(payload, path) }
  catch (error) {
    if (error instanceof ContractError)
      throw new ApiError(response.status, 'InvalidResponse', `The local app returned an unexpected response: ${error.message}`, { cause: error })
    throw error
  }
}

export function getJson<T>(path: string, decoder: Decoder<T>, signal?: AbortSignal): Promise<T> {
  return requestJson(path, decoder, { cache: 'no-store', signal })
}

export function changeJson<T>(path: string, method: 'POST' | 'PUT', decoder: Decoder<T>, body?: unknown,
  signal?: AbortSignal): Promise<T> {
  return requestJson(path, decoder, {
    method,
    headers: localHeaders,
    body: body === undefined ? undefined : JSON.stringify(body),
    signal
  })
}

export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) return `${error.code}: ${error.message}`
  return error instanceof Error ? error.message : String(error)
}

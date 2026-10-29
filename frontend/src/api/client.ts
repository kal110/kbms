import { config } from '../app/config'
import { sessionStore } from '../auth/sessionStore'

// Thin fetch wrapper for KnowledgeBase.Api. It adds the signed-in account's bearer token, turns
// RFC 9457 ProblemDetails into ApiError, and speaks the API's ETag / If-Match and
// Idempotency-Key conventions (backend README, "HTTP conventions").

export type QueryValue = string | number | boolean | null | undefined
export type Query = Record<string, QueryValue>

export interface RequestOptions {
  query?: Query
  body?: unknown
  /** The ETag you loaded: a body's `eTag`/`etag` string, or a submission's numeric `version`. */
  ifMatch?: string | number | null
  /** Retries with the same key replay the first response. `true` generates a fresh key. */
  idempotencyKey?: string | true
  signal?: AbortSignal
  /** Send without a token (dev sign-in endpoints). */
  anonymous?: boolean
  /** Use this token instead of the active account's (signing in a new account). */
  token?: string
}

export interface ApiResponse<T> {
  data: T
  etag: string | null
  status: number
}

/** An error response from the API (ProblemDetails), or status 0 when the API couldn't be reached. */
export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly title: string
  readonly detail?: string
  /** Validation errors per field (400). */
  readonly errors?: Record<string, string[]>
  readonly traceId?: string
  /** The whole ProblemDetails body, for extensions such as `duplicateTypes`. */
  readonly problem?: Record<string, unknown>

  constructor(status: number, code: string, title: string, problem?: Record<string, unknown>) {
    const detail = typeof problem?.detail === 'string' ? problem.detail : undefined
    super(detail ?? title)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.title = title
    this.detail = detail
    this.errors = problem?.errors as Record<string, string[]> | undefined
    this.traceId = typeof problem?.traceId === 'string' ? problem.traceId : undefined
    this.problem = problem
  }

  get isNotFound() {
    return this.status === 404
  }

  get isForbidden() {
    return this.status === 403
  }

  /** 412 / 409 stale: someone else changed the resource since it was loaded. */
  get isStale() {
    return this.status === 412 || this.code === 'concurrent-update' || this.code === 'stale-version'
  }
}

export const apiUrl = (path: string) => `${config.apiBaseUrl}/api${path}`

export function toQueryString(query?: Query) {
  if (!query) return ''
  const params = new URLSearchParams()
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null || value === '') continue
    params.set(key, String(value))
  }
  const text = params.toString()
  return text ? `?${text}` : ''
}

const formatIfMatch = (value: string | number) => (typeof value === 'number' ? `"${value}"` : value)

/**
 * Sends a request with the active account's token and the API's headers, and throws ApiError for an
 * error response. The caller reads the body: JSON in `request`, an event stream for the assistant.
 */
export async function send(method: string, path: string, options: RequestOptions = {}, accept = 'application/json'): Promise<Response> {
  const headers = new Headers({ Accept: accept })
  const account = options.anonymous ? null : sessionStore.getActiveAccount()
  const token = options.token ?? account?.token
  if (!options.anonymous && token) headers.set('Authorization', `Bearer ${token}`)
  if (options.body !== undefined) headers.set('Content-Type', 'application/json')
  if (options.ifMatch !== undefined && options.ifMatch !== null) headers.set('If-Match', formatIfMatch(options.ifMatch))
  if (options.idempotencyKey) {
    headers.set('Idempotency-Key', options.idempotencyKey === true ? crypto.randomUUID() : options.idempotencyKey)
  }

  let response: Response
  try {
    response = await fetch(apiUrl(path) + toQueryString(options.query), {
      method,
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      signal: options.signal,
    })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') throw error
    throw new ApiError(0, 'network', `Can't reach the server at ${config.apiBaseUrl}.`)
  }

  if (!response.ok) {
    const problem = await readJson<Record<string, unknown>>(response)
    const code = typeof problem?.code === 'string' ? problem.code : `http-${response.status}`
    const title = typeof problem?.title === 'string' ? problem.title : response.statusText || `Request failed (${response.status})`
    // The token was refused: this account has to sign in again. Other tabs and accounts are unaffected.
    if (response.status === 401 && account && !options.token) sessionStore.markExpired(account.externalId)
    throw new ApiError(response.status, code, title, problem)
  }
  return response
}

export async function request<T>(method: string, path: string, options: RequestOptions = {}): Promise<ApiResponse<T>> {
  const response = await send(method, path, options)
  return {
    data: (response.status === 204 ? undefined : await readJson<T>(response)) as T,
    etag: response.headers.get('ETag'),
    status: response.status,
  }
}

async function readJson<T>(response: Response): Promise<T | undefined> {
  const text = await response.text()
  if (!text) return undefined
  try {
    return JSON.parse(text) as T
  } catch {
    return undefined
  }
}

export const api = {
  get: <T>(path: string, options?: RequestOptions) => request<T>('GET', path, options).then((r) => r.data),
  post: <T>(path: string, body?: unknown, options?: RequestOptions) =>
    request<T>('POST', path, { ...options, body }).then((r) => r.data),
  put: <T>(path: string, body?: unknown, options?: RequestOptions) =>
    request<T>('PUT', path, { ...options, body }).then((r) => r.data),
  patch: <T>(path: string, body?: unknown, options?: RequestOptions) =>
    request<T>('PATCH', path, { ...options, body }).then((r) => r.data),
  delete: <T = void>(path: string, options?: RequestOptions) => request<T>('DELETE', path, options).then((r) => r.data),
}

/** A sentence for the user from any thrown value. */
export function errorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    const fieldErrors = error.errors ? Object.values(error.errors).flat() : []
    if (fieldErrors.length) return fieldErrors.join(' ')
    return error.detail && error.detail !== error.title ? `${error.title} ${error.detail}` : error.title
  }
  if (error instanceof Error) return error.message
  return 'Something went wrong.'
}

export const isAbort = (error: unknown) => error instanceof DOMException && error.name === 'AbortError'

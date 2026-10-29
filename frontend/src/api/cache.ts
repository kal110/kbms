import { sessionStore } from '../auth/sessionStore'

// Reference lists (document types, calendar, org tree…) are read by most screens and rarely change,
// so each account loads them once. Screens that edit them call invalidate() afterwards.

const entries = new Map<string, Promise<unknown>>()

const accountKey = () => sessionStore.getState().activeId ?? 'anonymous'

export function cached<T>(key: string, load: () => Promise<T>): Promise<T> {
  const fullKey = `${accountKey()}|${key}`
  let promise = entries.get(fullKey) as Promise<T> | undefined
  if (!promise) {
    promise = load()
    entries.set(fullKey, promise)
    // Don't keep failures: the next caller tries again.
    promise.catch(() => entries.delete(fullKey))
  }
  return promise
}

/** Forgets cached entries whose key starts with `prefix`, for every account. */
export function invalidate(prefix: string) {
  for (const key of entries.keys()) {
    if (key.slice(key.indexOf('|') + 1).startsWith(prefix)) entries.delete(key)
  }
}

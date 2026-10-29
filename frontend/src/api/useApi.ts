import { useCallback, useEffect, useRef, useState } from 'react'
import type { DependencyList, Dispatch, SetStateAction } from 'react'
import { isAbort } from './client'

export interface ApiState<T> {
  data: T | undefined
  error: unknown
  /** True on the first load and while reloading (data keeps the previous value meanwhile). */
  loading: boolean
  reload: () => void
  /** Replace the data locally, e.g. with what a save returned. */
  setData: Dispatch<SetStateAction<T | undefined>>
}

interface Result<T> {
  /** The deps (plus reload count) this result answers. */
  key: readonly unknown[]
  data?: T
  error?: unknown
}

const sameKey = (a: readonly unknown[], b: readonly unknown[]) => a.length === b.length && a.every((v, i) => Object.is(v, b[i]))

/**
 * Loads data for a screen and reloads it when `deps` change. The previous request is aborted, so a
 * slow answer for an old folder or filter never overwrites a newer one.
 *
 *   const folder = useApi((signal) => getFolder(folderId, signal), [folderId])
 */
export function useApi<T>(load: (signal: AbortSignal) => Promise<T>, deps: DependencyList): ApiState<T> {
  const [attempt, setAttempt] = useState(0)
  const [result, setResult] = useState<Result<T>>({ key: [] })
  const loadRef = useRef(load)
  const key = [...deps, attempt]

  useEffect(() => {
    loadRef.current = load
  })

  useEffect(() => {
    const controller = new AbortController()
    loadRef.current(controller.signal).then(
      (data) => {
        if (!controller.signal.aborted) setResult({ key, data })
      },
      (error: unknown) => {
        if (!controller.signal.aborted && !isAbort(error)) setResult((r) => ({ key, data: r.data, error }))
      },
    )
    return () => controller.abort()
    // Reloads when the caller's deps (or the reload count) change, like useMemo's deps.
    // oxlint-disable-next-line react-hooks/exhaustive-deps
  }, key)

  const current = sameKey(result.key, key)
  const reload = useCallback(() => setAttempt((n) => n + 1), [])
  const setData = useCallback<Dispatch<SetStateAction<T | undefined>>>(
    (value) =>
      setResult((r) => ({ ...r, data: typeof value === 'function' ? (value as (prev: T | undefined) => T | undefined)(r.data) : value })),
    [],
  )

  return { data: result.data, error: current ? result.error : undefined, loading: !current, reload, setData }
}

export interface ActionState<A extends unknown[], R> {
  /** Runs the action; resolves to its result, or undefined when it failed (see `error`). */
  run: (...args: A) => Promise<R | undefined>
  pending: boolean
  error: unknown
  clearError: () => void
}

/**
 * A button's server action with its pending and error state.
 *
 *   const approve = useAction((comment: string) => approveSubmission(id, version, { comments: comment, reviewedVersionIds: null }))
 *   <Button disabled={approve.pending} onClick={async () => { if (await approve.run(text)) close() }}>
 */
export function useAction<A extends unknown[], R>(action: (...args: A) => Promise<R>): ActionState<A, R> {
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<unknown>(undefined)
  const actionRef = useRef(action)

  useEffect(() => {
    actionRef.current = action
  })

  const run = useCallback(async (...args: A) => {
    setPending(true)
    setError(undefined)
    try {
      return await actionRef.current(...args)
    } catch (reason) {
      setError(reason)
      return undefined
    } finally {
      setPending(false)
    }
  }, [])

  const clearError = useCallback(() => setError(undefined), [])
  return { run, pending, error, clearError }
}

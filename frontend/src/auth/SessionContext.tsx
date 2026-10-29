import { createContext, useCallback, useContext, useEffect, useMemo, useState, useSyncExternalStore } from 'react'
import type { ReactNode } from 'react'
import { requestDevToken } from '../api/auth'
import { ApiError } from '../api/client'
import { getMe } from '../api/org'
import type { MeDto } from '../api/types'
import { isUsable, sessionStore } from './sessionStore'
import type { Account } from './sessionStore'

export type SessionStatus = 'signed-out' | 'expired' | 'loading' | 'ready' | 'error'

interface SessionContextValue {
  /** Every account signed in on this browser. */
  accounts: readonly Account[]
  /** The account this tab works as. */
  account: Account | null
  /** That account's profile, roles and capabilities (ready once status is 'ready'). */
  me: MeDto | null
  status: SessionStatus
  error: unknown
  retry: () => void
  switchTo: (externalId: string) => void
  signOut: (externalId: string) => void
  signOutAll: () => void
  /** Development sign-in: issues a token for a seeded person and makes them this tab's account. */
  devSignIn: (externalId: string) => Promise<Account>
}

const SessionContext = createContext<SessionContextValue | null>(null)

interface Profile {
  token: string
  me?: MeDto
  error?: unknown
}

async function devSignIn(externalId: string): Promise<Account> {
  const response = await requestDevToken(externalId)
  const account: Account = {
    externalId: response.user.externalId,
    displayName: response.user.displayName,
    initials: response.user.initials,
    email: response.user.email,
    title: response.user.title,
    token: response.accessToken,
    expiresAt: response.expiresAt,
  }
  sessionStore.addAccount(account)
  return account
}

/**
 * `signInAs`: a demo link (?as=…) named an account that isn't signed in yet; sign it in first
 * (development sign-in only). main.tsx already switched to it when it was signed in.
 */
export function SessionProvider({ signInAs, children }: { signInAs?: string | null; children: ReactNode }) {
  const state = useSyncExternalStore(sessionStore.subscribe, sessionStore.getState)
  const account = state.accounts.find((a) => a.externalId === state.activeId) ?? null
  // Profiles per account, so switching back to an account doesn't wait for /api/me again.
  const [profiles, setProfiles] = useState<Record<string, Profile>>({})
  const [attempt, setAttempt] = useState(0)
  const [demoPending, setDemoPending] = useState(Boolean(signInAs))

  useEffect(() => {
    if (!signInAs) return
    let cancelled = false
    devSignIn(signInAs)
      .catch(() => undefined) // Unknown id or no dev sign-in: carry on with the current account.
      .finally(() => {
        if (!cancelled) setDemoPending(false)
      })
    return () => {
      cancelled = true
    }
  }, [signInAs])

  const usable = isUsable(account)
  const externalId = account?.externalId
  const token = account?.token

  useEffect(() => {
    if (!usable || !externalId || !token) return
    const controller = new AbortController()
    getMe({ signal: controller.signal, token }).then(
      (me) => {
        setProfiles((p) => ({ ...p, [externalId]: { token, me } }))
        sessionStore.updateProfile(externalId, {
          displayName: me.displayName,
          initials: me.initials,
          email: me.email,
          title: me.title,
          roleLabel: me.roleLabel,
        })
      },
      (error: unknown) => {
        if (controller.signal.aborted) return
        // The API refused the token: this account must sign in again.
        if (error instanceof ApiError && error.status === 401) sessionStore.markExpired(externalId)
        setProfiles((p) => ({ ...p, [externalId]: { token, me: p[externalId]?.me, error } }))
      },
    )
    return () => controller.abort()
  }, [usable, externalId, token, attempt])

  const profile = externalId ? profiles[externalId] : undefined
  const me = usable && profile?.me ? profile.me : null
  const status: SessionStatus = demoPending
    ? 'loading'
    : !account
      ? 'signed-out'
      : !usable
        ? 'expired'
        : me
          ? 'ready'
          : profile?.error && profile.token === token
            ? 'error'
            : 'loading'

  const retry = useCallback(() => {
    if (externalId) setProfiles((p) => (p[externalId] ? { ...p, [externalId]: { ...p[externalId], error: undefined } } : p))
    setAttempt((n) => n + 1)
  }, [externalId, setProfiles, setAttempt])

  const value = useMemo<SessionContextValue>(
    () => ({
      accounts: state.accounts,
      account,
      me,
      status,
      error: profile?.error,
      retry,
      switchTo: sessionStore.switchTo,
      signOut: sessionStore.signOut,
      signOutAll: sessionStore.signOutAll,
      devSignIn,
    }),
    [state.accounts, account, me, status, profile?.error, retry],
  )

  return <SessionContext value={value}>{children}</SessionContext>
}

// oxlint-disable-next-line react/only-export-components
export function useSession() {
  const ctx = useContext(SessionContext)
  if (!ctx) throw new Error('useSession must be used inside <SessionProvider>')
  return ctx
}

/** The signed-in user. Only for screens inside the signed-in shell, where it's always loaded. */
// oxlint-disable-next-line react/only-export-components
export function useMe(): MeDto {
  const { me } = useSession()
  if (!me) throw new Error('useMe is only available inside the signed-in app shell')
  return me
}

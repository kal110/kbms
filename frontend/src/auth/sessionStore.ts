// Signed-in accounts, like a browser's account switcher: several people can be signed in at once
// and each tab works as one of them.
//
// - Accounts (with their tokens) live in localStorage, so every tab shares them and a new sign-in
//   or sign-out in one tab reaches the others.
// - The account a tab works as lives in sessionStorage, so two tabs can be two different people
//   (e.g. submit as faculty in one, review as the AS in the other). New tabs start with the account
//   used last.
//
// Plain module (no React) so the API client can read the active token and flag an expired one;
// React reads it through useSyncExternalStore in SessionProvider.

export interface Account {
  externalId: string
  displayName: string
  initials: string
  email: string | null
  title: string | null
  /** "Academic Supervisor · BSCS", filled in from /api/me after sign-in. */
  roleLabel?: string
  token: string
  /** ISO date-time the token stops working. */
  expiresAt: string
  /** The API refused the token (401): sign in again to keep using this account. */
  expired?: boolean
}

export interface SessionState {
  accounts: readonly Account[]
  /** The account this tab works as, or null when signed out here. */
  activeId: string | null
}

const ACCOUNTS_KEY = 'kbms.accounts'
const LAST_KEY = 'kbms.lastAccount'
const TAB_KEY = 'kbms.activeAccount'

function read(storage: () => Storage, key: string): string | null {
  try {
    return storage().getItem(key)
  } catch {
    // Storage can be unavailable (private window, blocked site data).
    return null
  }
}

function write(storage: () => Storage, key: string, value: string | null) {
  try {
    if (value === null) storage().removeItem(key)
    else storage().setItem(key, value)
  } catch {
    // Ignore: the session just won't survive a reload.
  }
}

const local = () => window.localStorage
const tab = () => window.sessionStorage

function readAccounts(): Account[] {
  const raw = read(local, ACCOUNTS_KEY)
  if (!raw) return []
  try {
    const parsed = JSON.parse(raw) as Account[]
    return Array.isArray(parsed) ? parsed.filter((a) => a && typeof a.externalId === 'string' && typeof a.token === 'string') : []
  } catch {
    return []
  }
}

/** Signed in with a token that hasn't expired or been refused. */
export const isUsable = (account: Account | undefined | null): boolean =>
  Boolean(account && !account.expired && Date.parse(account.expiresAt) > Date.now())

function initialState(): SessionState {
  const accounts = readAccounts()
  const has = (id: string | null) => Boolean(id && accounts.some((a) => a.externalId === id))
  const tabId = read(tab, TAB_KEY)
  const lastId = read(local, LAST_KEY)
  return { accounts, activeId: has(tabId) ? tabId : has(lastId) ? lastId : null }
}

let state: SessionState = initialState()
const listeners = new Set<() => void>()

function setState(next: SessionState, persistAccounts: boolean) {
  state = next
  if (persistAccounts) write(local, ACCOUNTS_KEY, JSON.stringify(next.accounts))
  write(tab, TAB_KEY, next.activeId)
  if (next.activeId) write(local, LAST_KEY, next.activeId)
  listeners.forEach((listener) => listener())
}

// Another tab signed someone in or out, or refreshed a token.
window.addEventListener('storage', (event) => {
  if (event.key !== ACCOUNTS_KEY && event.key !== null) return
  const accounts = readAccounts()
  const activeId = accounts.some((a) => a.externalId === state.activeId) ? state.activeId : null
  setState({ accounts, activeId }, false)
})

export const sessionStore = {
  subscribe(listener: () => void) {
    listeners.add(listener)
    return () => listeners.delete(listener)
  },

  getState: (): SessionState => state,

  getActiveAccount: (): Account | null => state.accounts.find((a) => a.externalId === state.activeId) ?? null,

  /** Adds the account (or refreshes its token) and makes it this tab's account. */
  addAccount(account: Account) {
    const existing = state.accounts.find((a) => a.externalId === account.externalId)
    const merged: Account = { ...existing, ...account, expired: false }
    const accounts = existing
      ? state.accounts.map((a) => (a.externalId === account.externalId ? merged : a))
      : [...state.accounts, merged]
    setState({ accounts, activeId: account.externalId }, true)
  },

  switchTo(externalId: string) {
    if (!state.accounts.some((a) => a.externalId === externalId) || state.activeId === externalId) return
    setState({ ...state, activeId: externalId }, false)
  },

  /** Keeps profile details from /api/me so the account switcher shows the role. */
  updateProfile(externalId: string, profile: Partial<Pick<Account, 'displayName' | 'initials' | 'email' | 'title' | 'roleLabel'>>) {
    const current = state.accounts.find((a) => a.externalId === externalId)
    if (!current) return
    const changed = (Object.keys(profile) as (keyof typeof profile)[]).some((k) => profile[k] !== current[k])
    if (!changed) return
    setState({ ...state, accounts: state.accounts.map((a) => (a === current ? { ...a, ...profile } : a)) }, true)
  },

  /**
   * The API refused `token`. Only that token: a request still carrying the old one can come back
   * after a password change has already stored the new one.
   */
  markExpired(externalId: string, token: string) {
    const current = state.accounts.find((a) => a.externalId === externalId)
    if (!current || current.expired || current.token !== token) return
    setState({ ...state, accounts: state.accounts.map((a) => (a === current ? { ...a, expired: true } : a)) }, true)
  },

  /** Signs one account out everywhere. This tab moves to another signed-in account, if any. */
  signOut(externalId: string) {
    const accounts = state.accounts.filter((a) => a.externalId !== externalId)
    const activeId =
      state.activeId === externalId ? (accounts.find(isUsable)?.externalId ?? null) : state.activeId
    if (read(local, LAST_KEY) === externalId) write(local, LAST_KEY, null)
    setState({ accounts, activeId }, true)
  },

  signOutAll() {
    write(local, LAST_KEY, null)
    setState({ accounts: [], activeId: null }, true)
  },
}

import { useMemo, useState } from 'react'
import { ArrowLeft, ChevronRight, LogOut, Search, UserPlus, X } from 'lucide-react'
import { useNavigate, useSearchParams } from 'react-router'
import { listDevUsers } from '../../api/auth'
import { ApiError, errorMessage } from '../../api/client'
import type { DevUser } from '../../api/types'
import { useApi } from '../../api/useApi'
import { AuthLayout } from '../../auth/AuthLayout'
import { useSession } from '../../auth/SessionContext'
import { isUsable } from '../../auth/sessionStore'
import type { Account } from '../../auth/sessionStore'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import styles from './SignInPage.module.css'

const roleNames: Record<string, string> = {
  SystemAdmin: 'System admin',
  Vpaf: 'VPAF',
  Dean: 'Dean',
  AcademicSupervisor: 'Academic Supervisor',
  DepartmentHead: 'Head / Director',
}

/** Only same-app paths, so ?next= can't send people to another site. */
function safeNext(value: string | null) {
  return value && value.startsWith('/') && !value.startsWith('//') && !value.startsWith('/signin') ? value : '/'
}

/**
 * Sign in, and choose between accounts already signed in on this browser.
 *   /signin                 choose an account (or pick a person when none is signed in)
 *   /signin?add=1           sign in another account
 *   /signin?account=<id>    that account's session ended: sign it in again
 *   &next=/path             where to go afterwards
 */
export function SignInPage() {
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const { accounts, account: current, switchTo, signOut, signOutAll, devSignIn } = useSession()
  const next = safeNext(params.get('next'))
  const expiredId = params.get('account')
  const [adding, setAdding] = useState(params.get('add') === '1')
  const [busyId, setBusyId] = useState<string | null>(null)
  const [error, setError] = useState<unknown>(undefined)

  const choosing = accounts.length > 0 && !adding
  const expiredAccount = accounts.find((a) => a.externalId === expiredId && !isUsable(a))

  async function signInAs(externalId: string) {
    setBusyId(externalId)
    setError(undefined)
    try {
      await devSignIn(externalId)
      navigate(next, { replace: true })
    } catch (e) {
      setError(e)
    } finally {
      setBusyId(null)
    }
  }

  function choose(account: Account) {
    if (isUsable(account)) {
      switchTo(account.externalId)
      navigate(next, { replace: true })
    } else {
      void signInAs(account.externalId)
    }
  }

  if (choosing) {
    return (
      <AuthLayout title="Choose an account" description="to continue to Academia KB">
        {expiredAccount && (
          <p className={styles.notice}>
            The session for <strong>{expiredAccount.displayName}</strong> ended. Choose it to sign in again.
          </p>
        )}
        <ul className={styles.list}>
          {accounts.map((a) => {
            const usable = isUsable(a)
            return (
              <li key={a.externalId} className={styles.row}>
                <button type="button" className={styles.person} disabled={busyId !== null} onClick={() => choose(a)}>
                  <span className={styles.avatar}>{a.initials}</span>
                  <span className={styles.text}>
                    <span className={styles.name}>{a.displayName}</span>
                    <span className={styles.meta}>{a.roleLabel ?? a.title ?? a.email ?? a.externalId}</span>
                  </span>
                  {busyId === a.externalId ? (
                    <span className={styles.state}>Signing in…</span>
                  ) : !usable ? (
                    <span className={`${styles.state} ${styles.stateWarn}`}>Signed out</span>
                  ) : a.externalId === current?.externalId ? (
                    <span className={styles.state}>This tab</span>
                  ) : (
                    <ChevronRight size={16} aria-hidden className={styles.chevron} />
                  )}
                </button>
                <button
                  type="button"
                  className={styles.remove}
                  aria-label={`Sign out of ${a.displayName}`}
                  title="Sign out of this account"
                  onClick={() => signOut(a.externalId)}
                >
                  <X size={16} aria-hidden />
                </button>
              </li>
            )
          })}
          <li className={styles.row}>
            <button type="button" className={styles.person} onClick={() => setAdding(true)}>
              <span className={`${styles.avatar} ${styles.avatarGhost}`}>
                <UserPlus size={16} aria-hidden />
              </span>
              <span className={styles.name}>Use another account</span>
            </button>
          </li>
        </ul>
        <InlineError error={error} />
        <footer className={styles.footer}>
          <span className={styles.hint}>Each browser tab can work as a different signed-in account.</span>
          {accounts.length > 1 && (
            <button type="button" className={styles.linkButton} onClick={signOutAll}>
              <LogOut size={14} aria-hidden />
              Sign out of all accounts
            </button>
          )}
        </footer>
      </AuthLayout>
    )
  }

  return (
    <AuthLayout
      title={accounts.length ? 'Add another account' : 'Sign in'}
      description="Development sign-in: choose one of the seeded people. In production this is the university's single sign-on."
    >
      <PeoplePicker
        signedIn={new Set(accounts.filter(isUsable).map((a) => a.externalId))}
        busyId={busyId}
        onPick={(user) => void signInAs(user.externalId)}
      />
      <InlineError error={error} />
      {accounts.length > 0 && (
        <button type="button" className={styles.linkButton} onClick={() => setAdding(false)}>
          <ArrowLeft size={14} aria-hidden />
          Back to your accounts
        </button>
      )}
    </AuthLayout>
  )
}

function PeoplePicker({
  signedIn,
  busyId,
  onPick,
}: {
  signedIn: Set<string>
  busyId: string | null
  onPick: (user: DevUser) => void
}) {
  const users = useApi((signal) => listDevUsers(signal), [])
  const [query, setQuery] = useState('')

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase()
    const list = users.data ?? []
    if (!q) return list
    return list.filter((u) =>
      [u.displayName, u.email, u.title, u.externalId, ...u.roles.map((r) => roleNames[r] ?? r)]
        .filter(Boolean)
        .some((v) => v!.toLowerCase().includes(q)),
    )
  }, [users.data, query])

  if (users.error) {
    if (users.error instanceof ApiError && users.error.status === 404) {
      return (
        <p className={styles.notice}>
          This API doesn&apos;t offer development sign-in: it runs with an identity provider (<code>Auth:Authority</code>),
          and single sign-on isn&apos;t connected to this app yet. {errorMessage(users.error)}
        </p>
      )
    }
    return <ErrorState error={users.error} onRetry={users.reload} />
  }
  if (!users.data) return <Loading label="Loading people…" />

  return (
    <div className={styles.picker}>
      <label className={styles.search}>
        <Search size={16} aria-hidden className={styles.searchIcon} />
        <span className="visually-hidden">Find a person</span>
        <input
          className={styles.searchInput}
          type="search"
          placeholder="Find by name, role or email"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          autoFocus
        />
      </label>
      <ul className={`${styles.list} ${styles.scroll}`}>
        {filtered.map((u) => (
          <li key={u.externalId} className={styles.row}>
            <button type="button" className={styles.person} disabled={busyId !== null} onClick={() => onPick(u)}>
              <span className={styles.avatar}>{u.initials}</span>
              <span className={styles.text}>
                <span className={styles.name}>{u.displayName}</span>
                <span className={styles.meta}>
                  {[u.title, u.email].filter(Boolean).join(' · ') || u.externalId}
                </span>
              </span>
              {u.roles.map((r) => (
                <span key={r} className={styles.role}>
                  {roleNames[r] ?? r}
                </span>
              ))}
              {busyId === u.externalId ? (
                <span className={styles.state}>Signing in…</span>
              ) : signedIn.has(u.externalId) ? (
                <span className={styles.state}>Signed in</span>
              ) : null}
            </button>
          </li>
        ))}
        {filtered.length === 0 && <li className={styles.empty}>No one matches “{query}”.</li>}
      </ul>
    </div>
  )
}

import { useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import { ArrowLeft, ChevronDown, ChevronRight, LogIn, LogOut, Search, UserPlus, X } from 'lucide-react'
import { useNavigate, useSearchParams } from 'react-router'
import { getSignInOptions, listDevUsers } from '../../api/auth'
import { ApiError } from '../../api/client'
import type { DevUser } from '../../api/types'
import { useApi } from '../../api/useApi'
import { AuthLayout } from '../../auth/AuthLayout'
import { useSession } from '../../auth/SessionContext'
import { isUsable } from '../../auth/sessionStore'
import type { Account } from '../../auth/sessionStore'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Button } from '../../components/Button'
import { PasswordField, TextField } from '../../components/Field'
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
 *   /signin                 choose an account (or sign in when none is signed in)
 *   /signin?add=1           sign in another account
 *   /signin?account=<id>    that account's session ended: sign it in again
 *   &next=/path             where to go afterwards
 * People sign in with a password; in Development the API also offers a pick-a-person list.
 */
export function SignInPage() {
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const { accounts, account: current, switchTo, signOut, signOutAll, devSignIn } = useSession()
  const options = useApi((signal) => getSignInOptions(signal), [])
  const next = safeNext(params.get('next'))
  const expiredId = params.get('account')
  const [adding, setAdding] = useState(params.get('add') === '1')
  const [login, setLogin] = useState(expiredId ?? '')
  const [busyId, setBusyId] = useState<string | null>(null)
  const [error, setError] = useState<unknown>(undefined)

  const choosing = accounts.length > 0 && !adding
  const expiredAccount = accounts.find((a) => a.externalId === expiredId && !isUsable(a))
  const devPicker = options.data?.devSignIn ?? false
  const done = () => navigate(next, { replace: true })

  async function signInAs(externalId: string) {
    setBusyId(externalId)
    setError(undefined)
    try {
      await devSignIn(externalId)
      done()
    } catch (e) {
      setError(e)
    } finally {
      setBusyId(null)
    }
  }

  function choose(account: Account) {
    if (isUsable(account)) {
      switchTo(account.externalId)
      done()
    } else if (devPicker) {
      void signInAs(account.externalId)
    } else {
      setLogin(account.externalId)
      setAdding(true)
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
            <button
              type="button"
              className={styles.person}
              onClick={() => {
                setLogin('')
                setAdding(true)
              }}
            >
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

  const passwords = options.data?.passwordSignIn ?? false
  return (
    <AuthLayout
      title={accounts.length ? 'Add another account' : 'Sign in'}
      description={passwords ? 'Use your sign-in id or email and your password.' : undefined}
    >
      {options.error ? (
        <ErrorState error={options.error} onRetry={options.reload} />
      ) : !options.data ? (
        <Loading label="Loading…" />
      ) : passwords ? (
        <PasswordForm key={login} initialLogin={login} onSignedIn={done} />
      ) : (
        <p className={styles.notice}>
          This API signs people in through an identity provider (<code>Auth:Authority</code>), and single sign-on
          isn&apos;t connected to this app yet.
        </p>
      )}
      {devPicker && (
        <DevSignIn
          signedIn={new Set(accounts.filter(isUsable).map((a) => a.externalId))}
          busyId={busyId}
          onPick={(user) => void signInAs(user.externalId)}
        />
      )}
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

/**
 * Sign-in id (or email) and password. A temporary password from an administrator turns the form
 * into "choose your own" (the API answers password-change-required), and signs in with that.
 */
function PasswordForm({ initialLogin, onSignedIn }: { initialLogin: string; onSignedIn: () => void }) {
  const { passwordSignIn } = useSession()
  const [login, setLogin] = useState(initialLogin)
  const [password, setPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [choosing, setChoosing] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<unknown>(undefined)

  const mismatch = choosing && confirm.length > 0 && confirm !== newPassword
  const fieldError = (field: string) => (error instanceof ApiError ? error.errors?.[field]?.[0] : undefined)

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (choosing && newPassword !== confirm) return
    setBusy(true)
    setError(undefined)
    try {
      await passwordSignIn(login.trim(), password, choosing ? newPassword : undefined)
      onSignedIn()
    } catch (err) {
      if (err instanceof ApiError && err.code === 'password-change-required') setChoosing(true)
      else setError(err)
    } finally {
      setBusy(false)
    }
  }

  if (choosing) {
    return (
      <form className={styles.form} onSubmit={(e) => void submit(e)}>
        <p className={styles.info}>
          An administrator set this password for your first sign-in. Choose your own to continue.
        </p>
        <PasswordField
          label="New password"
          value={newPassword}
          onChange={setNewPassword}
          autoComplete="new-password"
          autoFocus
          required
          error={fieldError('newPassword')}
          help="At least 8 characters. A few unrelated words make a strong one."
        />
        <PasswordField
          label="Type it again"
          value={confirm}
          onChange={setConfirm}
          autoComplete="new-password"
          required
          error={mismatch ? 'The two passwords don’t match.' : null}
        />
        {!(error instanceof ApiError && error.errors) && <InlineError error={error} />}
        <Button variant="primary" type="submit" className={styles.submit} disabled={busy || !newPassword || newPassword !== confirm}>
          {busy ? 'Signing in…' : 'Set password and sign in'}
        </Button>
        <button
          type="button"
          className={styles.linkButton}
          onClick={() => {
            setChoosing(false)
            setPassword('')
            setNewPassword('')
            setConfirm('')
            setError(undefined)
          }}
        >
          <ArrowLeft size={14} aria-hidden />
          Back
        </button>
      </form>
    )
  }

  return (
    <form className={styles.form} onSubmit={(e) => void submit(e)}>
      <TextField
        label="Sign-in id or email"
        value={login}
        onChange={setLogin}
        autoComplete="username"
        autoCapitalize="none"
        spellCheck={false}
        autoFocus={!initialLogin}
        required
        maxLength={320}
      />
      <PasswordField
        label="Password"
        value={password}
        onChange={setPassword}
        autoComplete="current-password"
        autoFocus={Boolean(initialLogin)}
        required
        maxLength={128}
      />
      <InlineError error={error} />
      <Button variant="primary" type="submit" icon={LogIn} className={styles.submit} disabled={busy || !login.trim() || !password}>
        {busy ? 'Signing in…' : 'Sign in'}
      </Button>
      <span className={styles.hint}>
        First time? Use the password your administrator gave you; you&apos;ll choose your own next. Forgot it? Ask a
        system administrator to set a new one.
      </span>
    </form>
  )
}

/** Development only: sign in as any seeded person, no password. */
function DevSignIn(props: { signedIn: Set<string>; busyId: string | null; onPick: (user: DevUser) => void }) {
  const [open, setOpen] = useState(false)
  return (
    <section className={styles.devSection}>
      <button type="button" className={styles.linkButton} aria-expanded={open} onClick={() => setOpen((o) => !o)}>
        {open ? <ChevronDown size={14} aria-hidden /> : <ChevronRight size={14} aria-hidden />}
        Development: sign in as a seeded person
      </button>
      {open && <PeoplePicker {...props} />}
    </section>
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

  if (users.error) return <ErrorState error={users.error} onRetry={users.reload} />
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

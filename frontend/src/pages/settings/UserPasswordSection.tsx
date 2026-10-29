import { useState } from 'react'
import type { FormEvent } from 'react'
import { KeyRound, Shuffle } from 'lucide-react'
import { getSignInOptions } from '../../api/auth'
import { cached } from '../../api/cache'
import { ApiError } from '../../api/client'
import { setUserPassword } from '../../api/org'
import type { UserAdminDto } from '../../api/types'
import { useAction, useApi } from '../../api/useApi'
import { InlineError } from '../../components/AsyncState'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { PasswordField } from '../../components/Field'
import { Modal } from '../../components/Modal'
import { Switch } from '../../components/Switch'
import panel from './panel.module.css'
import styles from './UsersPage.module.css'

const dateTime = (iso: string) =>
  new Date(iso).toLocaleString('en-US', { month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit' })

// No 0/O, 1/l/I: the password is read off a screen and typed in by someone else.
const ALPHABET = 'abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'

/** 12 random characters as xxxx-xxxx-xxxx. getRandomValues works over plain HTTP, unlike randomUUID. */
function generatePassword() {
  const chars: string[] = []
  const limit = 256 - (256 % ALPHABET.length) // no modulo bias
  while (chars.length < 12) {
    for (const b of crypto.getRandomValues(new Uint8Array(16))) {
      if (b < limit && chars.length < 12) chars.push(ALPHABET[b % ALPHABET.length])
    }
  }
  return [0, 4, 8].map((i) => chars.slice(i, i + 4).join('')).join('-')
}

/**
 * Settings → Users: their password, when people sign in to KBMS itself (no identity provider).
 * Shows whether they have one, a temporary one or a lockout, and sets or resets it.
 */
export function UserPasswordSection({ user, meId, onChanged }: { user: UserAdminDto; meId: string; onChanged: (user: UserAdminDto) => void }) {
  const options = useApi(() => cached('sign-in-options', () => getSignInOptions()), [])
  const [setting, setSetting] = useState(false)
  if (!options.data?.passwordSignIn) return null

  const password = user.password
  const self = user.id === meId
  return (
    <div className={styles.section}>
      <span className={panel.eyebrow}>Password</span>
      <span className={styles.passwordStatus}>
        {!password ? (
          <span className={panel.help}>No password yet: they can&apos;t sign in until you set one.</span>
        ) : password.mustChange ? (
          <span className={panel.help}>
            Temporary password, set {dateTime(password.changedAt)}. They choose their own at their next sign-in.
          </span>
        ) : (
          <span className={panel.help}>Their own password, set {dateTime(password.changedAt)}.</span>
        )}
        {password?.lockedUntil && (
          <Badge tone="warning">Locked until {new Date(password.lockedUntil).toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })}</Badge>
        )}
      </span>
      {password?.lockedUntil && (
        <span className={panel.help}>Locked after too many wrong passwords. A new password unlocks it now.</span>
      )}
      <span className={styles.passwordActions}>
        <Button
          size="sm"
          icon={KeyRound}
          disabled={self || !user.active}
          title={self ? 'Change your own password from your account menu' : !user.active ? 'Reactivate the account first' : undefined}
          onClick={() => setSetting(true)}
        >
          {password ? 'Reset password…' : 'Set password…'}
        </Button>
      </span>
      {setting && (
        <SetPasswordModal
          user={user}
          onDone={onChanged}
          onClose={() => setSetting(false)}
        />
      )}
    </div>
  )
}

function SetPasswordModal({ user, onDone, onClose }: { user: UserAdminDto; onDone: (user: UserAdminDto) => void; onClose: () => void }) {
  const [password, setPassword] = useState(generatePassword)
  const [mustChange, setMustChange] = useState(true)
  const [given, setGiven] = useState<string | null>(null)
  const save = useAction(setUserPassword)
  const fieldError = save.error instanceof ApiError ? save.error.errors?.password?.[0] : undefined

  async function submit(e: FormEvent) {
    e.preventDefault()
    const next = await save.run(user.id, password, mustChange)
    if (!next) return
    onDone(next)
    setGiven(password)
  }

  if (given) {
    return (
      <Modal title="Password set" onClose={onClose} footer={<Button variant="primary" onClick={onClose}>Done</Button>}>
        <div className={styles.passwordGiven}>
          <span>Give {user.displayName} this password, and their sign-in id <code>{user.externalId}</code>:</span>
          <code className={styles.passwordValue}>{given}</code>
          <span className={panel.help}>
            {mustChange
              ? 'They choose their own at their first sign-in. It isn’t shown again.'
              : 'It isn’t shown again.'}{' '}
            Anywhere they were signed in now asks for the new password.
          </span>
        </div>
      </Modal>
    )
  }

  return (
    <Modal
      title={user.password ? `Reset password · ${user.displayName}` : `Set password · ${user.displayName}`}
      description="Their open sessions end. There's no email here, so you hand the password over yourself."
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" type="submit" form="set-password" disabled={!password || save.pending}>
            {save.pending ? 'Saving…' : 'Set password'}
          </Button>
        </>
      }
    >
      <form id="set-password" className={styles.passwordForm} onSubmit={(e) => void submit(e)}>
        <div className={styles.passwordRow}>
          <PasswordField
            label="Password"
            value={password}
            onChange={setPassword}
            autoComplete="new-password"
            required
            maxLength={128}
            error={fieldError}
            help="Generated for you; type your own if you prefer (at least 8 characters)."
          />
          <Button size="sm" icon={Shuffle} onClick={() => setPassword(generatePassword())} className={styles.generate}>
            New one
          </Button>
        </div>
        <span className={styles.switchRow}>
          <Switch checked={mustChange} onChange={setMustChange} label="They choose their own at the next sign-in" />
          <span>They choose their own at the next sign-in</span>
        </span>
        {!fieldError && <InlineError error={save.error} />}
      </form>
    </Modal>
  )
}

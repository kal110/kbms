import { useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError } from '../api/client'
import { InlineError } from '../components/AsyncState'
import { Button } from '../components/Button'
import { PasswordField } from '../components/Field'
import { Modal } from '../components/Modal'
import { useSession } from './SessionContext'
import styles from './ChangePasswordModal.module.css'

/** Your own password: asks for the current one. Your other signed-in sessions end; this one stays. */
export function ChangePasswordModal({ onClose }: { onClose: () => void }) {
  const { changePassword } = useSession()
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [confirm, setConfirm] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<unknown>(undefined)
  const [done, setDone] = useState(false)

  const mismatch = confirm.length > 0 && confirm !== next
  const fieldError = (field: string) => (error instanceof ApiError ? error.errors?.[field]?.[0] : undefined)
  const ready = current && next && next === confirm && !busy

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!ready) return
    setBusy(true)
    setError(undefined)
    try {
      await changePassword(current, next)
      setDone(true)
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  if (done) {
    return (
      <Modal title="Password changed" onClose={onClose} footer={<Button variant="primary" onClick={onClose}>Done</Button>}>
        <p className={styles.text}>
          Use the new password from now on. Anywhere else this account was signed in now asks for it.
        </p>
      </Modal>
    )
  }

  return (
    <Modal
      title="Change password"
      description="Anywhere else this account is signed in will have to sign in again."
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" type="submit" form="change-password" disabled={!ready}>
            {busy ? 'Saving…' : 'Change password'}
          </Button>
        </>
      }
    >
      <form id="change-password" className={styles.form} onSubmit={(e) => void submit(e)}>
        <PasswordField
          label="Current password"
          value={current}
          onChange={setCurrent}
          autoComplete="current-password"
          autoFocus
          required
          error={fieldError('currentPassword')}
        />
        <PasswordField
          label="New password"
          value={next}
          onChange={setNext}
          autoComplete="new-password"
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
      </form>
    </Modal>
  )
}

import { useId, useState } from 'react'
import type { InputHTMLAttributes, ReactNode } from 'react'
import { Eye, EyeOff } from 'lucide-react'
import styles from './Field.module.css'

interface FieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'onChange' | 'value' | 'type'> {
  label: ReactNode
  value: string
  onChange: (value: string) => void
  /** Shown under the input instead of the help text. */
  error?: string | null
  help?: ReactNode
}

/** A labelled text input with its help or error line. */
export function TextField({ label, value, onChange, error, help, ...input }: FieldProps) {
  const id = useId()
  return (
    <div className={styles.field}>
      <label className={styles.label} htmlFor={id}>
        {label}
      </label>
      <input
        {...input}
        id={id}
        className={`${styles.input} ${error ? styles.invalid : ''}`}
        value={value}
        aria-invalid={Boolean(error)}
        onChange={(e) => onChange(e.target.value)}
      />
      <Note error={error} help={help} />
    </div>
  )
}

/** A password input with a show/hide toggle, so a long password can be checked before it's sent. */
export function PasswordField({ label, value, onChange, error, help, ...input }: FieldProps) {
  const id = useId()
  const [shown, setShown] = useState(false)
  const Icon = shown ? EyeOff : Eye
  return (
    <div className={styles.field}>
      <label className={styles.label} htmlFor={id}>
        {label}
      </label>
      <span className={styles.withToggle}>
        <input
          {...input}
          id={id}
          type={shown ? 'text' : 'password'}
          className={`${styles.input} ${error ? styles.invalid : ''}`}
          value={value}
          aria-invalid={Boolean(error)}
          spellCheck={false}
          onChange={(e) => onChange(e.target.value)}
        />
        <button
          type="button"
          className={styles.toggle}
          aria-label={shown ? 'Hide password' : 'Show password'}
          aria-pressed={shown}
          onClick={() => setShown((s) => !s)}
        >
          <Icon size={15} aria-hidden />
        </button>
      </span>
      <Note error={error} help={help} />
    </div>
  )
}

function Note({ error, help }: { error?: string | null; help?: ReactNode }) {
  if (error) return <span className={styles.error}>{error}</span>
  return help ? <span className={styles.help}>{help}</span> : null
}

import { AlertTriangle, Loader2, Lock, SearchX, WifiOff } from 'lucide-react'
import { ApiError, errorMessage } from '../api/client'
import { Button } from './Button'
import styles from './AsyncState.module.css'

/** Placeholder while a section's data loads. */
export function Loading({ label = 'Loading…', compact = false }: { label?: string; compact?: boolean }) {
  return (
    <div className={`${styles.box} ${compact ? styles.compact : ''}`} role="status">
      <Loader2 size={compact ? 16 : 20} className={styles.spin} aria-hidden />
      <span>{label}</span>
    </div>
  )
}

/** A load that failed: says why in plain words and offers a retry. */
export function ErrorState({ error, onRetry, compact = false }: { error: unknown; onRetry?: () => void; compact?: boolean }) {
  const api = error instanceof ApiError ? error : undefined
  const Icon = api?.status === 0 ? WifiOff : api?.isNotFound ? SearchX : api?.isForbidden ? Lock : AlertTriangle
  const title =
    api?.status === 0
      ? "Can't reach the server"
      : api?.isNotFound
        ? 'Not found'
        : api?.isForbidden
          ? "You don't have access"
          : "Couldn't load this"
  const text = api?.isNotFound ? "It doesn't exist, or it isn't shared with you." : errorMessage(error)
  return (
    <div className={`${styles.box} ${styles.error} ${compact ? styles.compact : ''}`} role="alert">
      <Icon size={compact ? 16 : 20} aria-hidden className={styles.errorIcon} />
      <div className={styles.text}>
        <strong>{title}</strong>
        <span className={styles.detail}>{text}</span>
      </div>
      {onRetry && api?.status !== 404 && api?.status !== 403 && (
        <Button size="sm" onClick={onRetry}>
          Try again
        </Button>
      )}
    </div>
  )
}

/** An action's error, shown inline next to the buttons that caused it. */
export function InlineError({ error }: { error: unknown }) {
  if (!error) return null
  return (
    <p className={styles.inline} role="alert">
      <AlertTriangle size={14} aria-hidden />
      {errorMessage(error)}
    </p>
  )
}

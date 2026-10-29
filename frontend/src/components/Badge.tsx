import type { ReactNode } from 'react'
import styles from './Badge.module.css'

export type BadgeTone = 'success' | 'warning' | 'danger' | 'info' | 'neutral' | 'purple'

interface BadgeProps {
  tone?: BadgeTone
  children: ReactNode
}

export function Badge({ tone = 'neutral', children }: BadgeProps) {
  return <span className={`${styles.badge} ${styles[tone]}`}>{children}</span>
}

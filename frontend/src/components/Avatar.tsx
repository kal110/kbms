import styles from './Avatar.module.css'

interface AvatarProps {
  initials: string
  size?: number
  color?: string
}

export function Avatar({ initials, size = 28, color = 'var(--primary)' }: AvatarProps) {
  return (
    <span
      className={styles.avatar}
      style={{ width: size, height: size, background: color, fontSize: Math.round(size * 0.36) }}
      aria-hidden
    >
      {initials}
    </span>
  )
}

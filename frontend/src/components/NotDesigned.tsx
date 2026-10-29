import type { LucideIcon } from 'lucide-react'
import { PencilRuler } from 'lucide-react'
import styles from './NotDesigned.module.css'

interface NotDesignedProps {
  title: string
  description?: string
  icon?: LucideIcon
}

/** Placeholder for screens the pmtool design doesn't cover yet. */
export function NotDesigned({ title, description, icon: Icon = PencilRuler }: NotDesignedProps) {
  return (
    <div className={styles.box}>
      <span className={styles.icon}>
        <Icon size={22} aria-hidden />
      </span>
      <h2 className={styles.title}>{title}</h2>
      <p className={styles.text}>{description ?? 'This screen is not in the design yet.'}</p>
    </div>
  )
}

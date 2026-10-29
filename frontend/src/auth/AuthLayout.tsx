import type { ReactNode } from 'react'
import { Library } from 'lucide-react'
import styles from './AuthLayout.module.css'

/** Centered card on the canvas, for sign-in and the moments before the app shell can show. */
export function AuthLayout({ title, description, children }: { title?: ReactNode; description?: ReactNode; children: ReactNode }) {
  return (
    <div className={styles.page}>
      <div className={styles.column}>
        <div className={styles.brand}>
          <span className={styles.logo}>
            <Library size={20} aria-hidden />
          </span>
          <span className={styles.brandText}>
            <span className={styles.brandName}>Academia KB</span>
            <span className={styles.brandSub}>Knowledge &amp; approvals</span>
          </span>
        </div>
        <main className={styles.card}>
          {(title || description) && (
            <header className={styles.header}>
              {title && <h1 className={styles.title}>{title}</h1>}
              {description && <p className={styles.description}>{description}</p>}
            </header>
          )}
          {children}
        </main>
      </div>
    </div>
  )
}

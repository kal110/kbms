import type { ReactNode } from 'react'
import styles from './PageHeader.module.css'

interface PageHeaderProps {
  breadcrumb?: ReactNode
  title: ReactNode
  description?: ReactNode
  actions?: ReactNode
}

export function PageHeader({ breadcrumb, title, description, actions }: PageHeaderProps) {
  return (
    <header className={styles.header}>
      <div className={styles.text}>
        {breadcrumb && <div className={styles.breadcrumb}>{breadcrumb}</div>}
        <h1 className={styles.title}>{title}</h1>
        {description && <p className={styles.description}>{description}</p>}
      </div>
      {actions && <div className={styles.actions}>{actions}</div>}
    </header>
  )
}

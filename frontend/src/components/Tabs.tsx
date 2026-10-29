import type { LucideIcon } from 'lucide-react'
import { NavLink } from 'react-router'
import styles from './Tabs.module.css'

export interface TabItem {
  key: string
  label: string
  icon?: LucideIcon
  count?: number
  /** When set the tab is a route link; otherwise it is controlled by `value`/`onChange`. */
  to?: string
  /** A route tab that is active only on its own path, not on paths below it. */
  end?: boolean
}

interface TabsProps {
  items: TabItem[]
  value?: string
  onChange?: (key: string) => void
}

export function Tabs({ items, value, onChange }: TabsProps) {
  return (
    <nav className={styles.tabs} role="tablist">
      {items.map((item) => {
        const content = (
          <>
            {item.icon && <item.icon size={15} aria-hidden />}
            {item.label}
            {item.count !== undefined && <span className={styles.count}>{item.count}</span>}
          </>
        )
        if (item.to) {
          return (
            <NavLink
              key={item.key}
              to={item.to}
              end={item.end}
              role="tab"
              className={({ isActive }) => `${styles.tab} ${isActive ? styles.active : ''}`}
            >
              {content}
            </NavLink>
          )
        }
        const active = item.key === value
        return (
          <button
            key={item.key}
            type="button"
            role="tab"
            aria-selected={active}
            className={`${styles.tab} ${active ? styles.active : ''}`}
            onClick={() => onChange?.(item.key)}
          >
            {content}
          </button>
        )
      })}
    </nav>
  )
}

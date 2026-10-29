import { Suspense, useEffect, useRef } from 'react'
import { Outlet, useLocation } from 'react-router'
import { useMe } from '../auth/SessionContext'
import { Sidebar } from './Sidebar'
import styles from './AppShell.module.css'

export function AppShell() {
  const { pathname } = useLocation()
  const mainRef = useRef<HTMLElement>(null)
  const me = useMe()

  // The main column is the scroll container, so reset it when moving to another screen.
  useEffect(() => {
    mainRef.current?.scrollTo(0, 0)
  }, [pathname])

  return (
    <div className={styles.shell}>
      <Sidebar />
      <main ref={mainRef} className={styles.main}>
        {/* Keyed by account: switching accounts remounts the screen, so nothing from the previous person stays. */}
        <Suspense key={me.id} fallback={<div className={styles.loading}>Loading…</div>}>
          <Outlet />
        </Suspense>
      </main>
    </div>
  )
}

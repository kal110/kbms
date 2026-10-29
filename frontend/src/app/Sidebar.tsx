import { useEffect, useRef, useState } from 'react'
import { ChevronsUpDown, Library, LogOut, UserPlus } from 'lucide-react'
import { NavLink, useNavigate } from 'react-router'
import { useMe, useSession } from '../auth/SessionContext'
import { isUsable } from '../auth/sessionStore'
import type { Account } from '../auth/sessionStore'
import { navFor } from './navigation'
import styles from './Sidebar.module.css'

export function Sidebar() {
  const me = useMe()
  const { accounts, account, switchTo, signOut, signOutAll } = useSession()
  const navigate = useNavigate()
  const [menuOpen, setMenuOpen] = useState(false)
  const userRef = useRef<HTMLDivElement>(null)
  const others = accounts.filter((a) => a.externalId !== account?.externalId)

  useEffect(() => {
    if (!menuOpen) return
    const onDown = (e: MouseEvent) => {
      if (!userRef.current?.contains(e.target as Node)) setMenuOpen(false)
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setMenuOpen(false)
    }
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [menuOpen])

  function switchAccount(target: Account) {
    setMenuOpen(false)
    if (isUsable(target)) {
      switchTo(target.externalId)
      // "/" lands on the new account's home page once its profile is loaded.
      navigate('/')
    } else {
      navigate(`/signin?account=${encodeURIComponent(target.externalId)}&next=/`)
    }
  }

  function signOutHere() {
    setMenuOpen(false)
    if (!account) return
    signOut(account.externalId)
    navigate('/')
  }

  return (
    <aside className={styles.sidebar}>
      <div className={styles.brand}>
        <span className={styles.logo}>
          <Library size={18} aria-hidden />
        </span>
        <span className={styles.brandText}>
          <span className={styles.brandName}>Academia KB</span>
          <span className={styles.brandSub}>Knowledge &amp; approvals</span>
        </span>
      </div>

      <nav className={styles.nav}>
        {navFor(me).map((section) => (
          <div key={section.label} className={styles.section}>
            <div className={styles.sectionLabel}>{section.label}</div>
            {section.items.map((item) => (
              <NavLink
                key={item.to}
                to={item.to}
                className={({ isActive }) => `${styles.item} ${isActive ? styles.active : ''}`}
              >
                <item.icon size={18} aria-hidden className={styles.itemIcon} />
                {item.label}
              </NavLink>
            ))}
          </div>
        ))}
      </nav>

      <div className={styles.userWrap} ref={userRef}>
        {menuOpen && (
          <div className={styles.menu} role="menu" aria-label="Accounts">
            <div className={styles.current}>
              <span className={styles.menuAvatar}>{me.initials}</span>
              <span className={styles.menuText}>
                <span className={styles.menuName}>{me.displayName}</span>
                <span className={styles.menuRole}>{me.email ?? me.roleLabel}</span>
              </span>
            </div>

            {others.length > 0 && (
              <>
                <div className={styles.menuLabel}>Switch account</div>
                {others.map((a) => (
                  <button key={a.externalId} type="button" role="menuitem" className={styles.menuItem} onClick={() => switchAccount(a)}>
                    <span className={styles.menuAvatar}>{a.initials}</span>
                    <span className={styles.menuText}>
                      <span className={styles.menuName}>{a.displayName}</span>
                      <span className={styles.menuRole}>{isUsable(a) ? (a.roleLabel ?? a.title) : 'Signed out · sign in again'}</span>
                    </span>
                  </button>
                ))}
              </>
            )}

            <div className={styles.menuDivider} />
            <button
              type="button"
              role="menuitem"
              className={styles.menuAction}
              onClick={() => {
                setMenuOpen(false)
                navigate('/signin?add=1&next=/')
              }}
            >
              <UserPlus size={16} aria-hidden />
              Add another account
            </button>
            <button type="button" role="menuitem" className={styles.menuAction} onClick={signOutHere}>
              <LogOut size={16} aria-hidden />
              Sign out of {me.displayName}
            </button>
            {others.length > 0 && (
              <button
                type="button"
                role="menuitem"
                className={styles.menuAction}
                onClick={() => {
                  setMenuOpen(false)
                  signOutAll()
                }}
              >
                <LogOut size={16} aria-hidden />
                Sign out of all accounts
              </button>
            )}
            <p className={styles.menuHint}>Each tab can work as a different account.</p>
          </div>
        )}
        <button
          type="button"
          className={styles.user}
          aria-haspopup="menu"
          aria-expanded={menuOpen}
          onClick={() => setMenuOpen((open) => !open)}
        >
          <span className={styles.avatar}>{me.initials}</span>
          <span className={styles.userText}>
            <span className={styles.userName}>{me.displayName}</span>
            <span className={styles.userRole}>{me.roleLabel}</span>
          </span>
          {others.length > 0 && (
            <span className={styles.accountCount} title={`${accounts.length} accounts signed in`}>
              {accounts.length}
            </span>
          )}
          <ChevronsUpDown size={16} aria-hidden className={styles.userChevron} />
        </button>
      </div>
    </aside>
  )
}

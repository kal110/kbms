import { useEffect } from 'react'
import type { ReactNode } from 'react'
import { X } from 'lucide-react'
import styles from './Modal.module.css'

interface ModalProps {
  title: ReactNode
  description?: ReactNode
  width?: number
  footer?: ReactNode
  onClose: () => void
  children: ReactNode
}

export function Modal({ title, description, width = 520, footer, onClose, children }: ModalProps) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className={styles.overlay} onMouseDown={onClose}>
      <div
        className={styles.dialog}
        role="dialog"
        aria-modal="true"
        style={{ width }}
        onMouseDown={(e) => e.stopPropagation()}
      >
        <header className={styles.header}>
          <div className={styles.headText}>
            <h2 className={styles.title}>{title}</h2>
            {description && <p className={styles.description}>{description}</p>}
          </div>
          <button type="button" className={styles.close} onClick={onClose} aria-label="Close">
            <X size={18} />
          </button>
        </header>
        <div className={styles.body}>{children}</div>
        {footer && <footer className={styles.footer}>{footer}</footer>}
      </div>
    </div>
  )
}

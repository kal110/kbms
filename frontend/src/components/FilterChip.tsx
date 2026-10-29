import { useEffect, useRef, useState } from 'react'
import { Check, ChevronDown } from 'lucide-react'
import styles from './FilterChip.module.css'

export interface FilterOption<T extends string> {
  value: T
  label: string
}

interface FilterChipProps<T extends string> {
  label: string
  value: T
  options: FilterOption<T>[]
  onChange: (value: T) => void
}

/** Pill-shaped filter with a dropdown menu, e.g. "Type: Policy ▾". */
export function FilterChip<T extends string>({ label, value, options, onChange }: FilterChipProps<T>) {
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)
  const current = options.find((o) => o.value === value)

  useEffect(() => {
    if (!open) return
    const onDown = (e: MouseEvent) => {
      if (!ref.current?.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false)
    }
    document.addEventListener('mousedown', onDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('mousedown', onDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  return (
    <div className={styles.wrap} ref={ref}>
      <button
        type="button"
        className={styles.chip}
        aria-haspopup="listbox"
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
      >
        {label}: {current?.label ?? value}
        <ChevronDown size={14} className={styles.chevron} aria-hidden />
      </button>
      {open && (
        <div className={styles.menu} role="listbox">
          {options.map((o) => (
            <button
              key={o.value}
              type="button"
              role="option"
              aria-selected={o.value === value}
              className={styles.option}
              onClick={() => {
                onChange(o.value)
                setOpen(false)
              }}
            >
              <span>{o.label}</span>
              {o.value === value && <Check size={14} className={styles.check} aria-hidden />}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

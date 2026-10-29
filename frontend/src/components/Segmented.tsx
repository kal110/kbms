import styles from './Segmented.module.css'

interface SegmentedProps<T extends string> {
  options: { value: T; label: string }[]
  value: T | null
  onChange: (value: T) => void
  /** md fills its row (forms); sm hugs its content (table cells, toolbars). */
  size?: 'md' | 'sm'
  disabled?: boolean
  ariaLabel?: string
}

export function Segmented<T extends string>({
  options,
  value,
  onChange,
  size = 'md',
  disabled,
  ariaLabel,
}: SegmentedProps<T>) {
  return (
    <div className={`${styles.group} ${styles[size]}`} role="radiogroup" aria-label={ariaLabel}>
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          role="radio"
          aria-checked={o.value === value}
          disabled={disabled}
          className={`${styles.seg} ${o.value === value ? styles.on : ''}`}
          onClick={() => onChange(o.value)}
        >
          {o.label}
        </button>
      ))}
    </div>
  )
}

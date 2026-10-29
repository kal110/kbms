import type { ButtonHTMLAttributes } from 'react'
import type { LucideIcon } from 'lucide-react'
import styles from './Button.module.css'

export type ButtonVariant = 'primary' | 'secondary' | 'success' | 'danger' | 'ghost'

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant
  size?: 'md' | 'sm'
  icon?: LucideIcon
  iconRight?: LucideIcon
}

export function Button({
  variant = 'secondary',
  size = 'md',
  icon: Icon,
  iconRight: IconRight,
  className,
  children,
  type = 'button',
  ...rest
}: ButtonProps) {
  const iconSize = size === 'sm' ? 14 : 16
  return (
    <button
      type={type}
      className={[styles.button, styles[variant], styles[size], className].filter(Boolean).join(' ')}
      {...rest}
    >
      {Icon && <Icon size={iconSize} aria-hidden />}
      {children}
      {IconRight && <IconRight size={iconSize} aria-hidden />}
    </button>
  )
}

import type { ReactNode } from 'react'
import type { LucideIcon } from 'lucide-react'
import { Folder as FolderIcon, FolderLock, FolderOpen, FolderTree as FolderTreeIcon } from 'lucide-react'
import type { FolderNode } from '../../api/types'
import styles from './FolderTree.module.css'

interface FolderTreeProps {
  label?: string
  /** Flat list from GET /api/folders/tree; nested by parentId. */
  folders: FolderNode[]
  selectedId?: string
  expanded: Set<string>
  onSelect: (folder: FolderNode) => void
  /** The org roots (Colleges, Non-academic departments) are shown as entry points, not expanded here. */
  collapseSystem?: boolean
  /** Colour of the lock icon for restricted / stopped-inheriting folders. */
  lockColor?: string
  children?: ReactNode
}

const isOrgRoot = (folder: FolderNode) => folder.kind === 'AcademicRoot' || folder.kind === 'OfficeRoot'

/** Restricted, or a subfolder that stopped inheriting its parent's access (top-level folders have nothing to inherit). */
const isLocked = (folder: FolderNode) => folder.isRestricted || (Boolean(folder.parentId) && !folder.inheritPermissions)

/** The org roots (Colleges, then Non-academic departments) lead the list; the library's own folders follow. */
const byOrder = (a: FolderNode, b: FolderNode) =>
  Number(isOrgRoot(b)) - Number(isOrgRoot(a)) || a.sortOrder - b.sortOrder || a.name.localeCompare(b.name)

/** Left-hand folder tree of the Library and Folder access screens (frames 1 and 13). */
export function FolderTree({
  label = 'Folders',
  folders,
  selectedId,
  expanded,
  onSelect,
  collapseSystem = true,
  lockColor,
  children,
}: FolderTreeProps) {
  const childrenOf = (id: string | null) => folders.filter((f) => (f.parentId ?? null) === id).sort(byOrder)

  const renderNode = (folder: FolderNode, depth: number): ReactNode => {
    const kids = collapseSystem && isOrgRoot(folder) ? [] : childrenOf(folder.id)
    const isOpen = expanded.has(folder.id) && kids.length > 0
    const selected = folder.id === selectedId
    const locked = isLocked(folder)
    let Icon = isOpen || selected ? FolderOpen : FolderIcon
    if (isOrgRoot(folder)) Icon = FolderTreeIcon
    else if (locked) Icon = FolderLock
    return (
      <div key={folder.id} className={styles.group}>
        <button
          type="button"
          className={`${styles.row} ${selected ? styles.selected : ''}`}
          style={{ paddingLeft: 8 + depth * 18 }}
          aria-expanded={kids.length > 0 ? isOpen : undefined}
          aria-current={selected ? 'true' : undefined}
          onClick={() => onSelect(folder)}
        >
          <Icon
            size={16}
            className={styles.icon}
            style={locked && !selected && lockColor ? { color: lockColor } : undefined}
            aria-hidden
          />
          <span className={styles.name}>{folder.name}</span>
        </button>
        {isOpen && kids.map((kid) => renderNode(kid, depth + 1))}
      </div>
    )
  }

  return (
    <nav className={styles.tree} aria-label={label}>
      <div className={styles.label}>{label}</div>
      {childrenOf(null).map((root) => renderNode(root, 0))}
      {children}
    </nav>
  )
}

export function TreeSectionLabel({ children }: { children: ReactNode }) {
  return <div className={`${styles.label} ${styles.sectionLabel}`}>{children}</div>
}

interface TreeItemProps {
  icon: LucideIcon
  label: string
  selected?: boolean
  onClick: () => void
}

export function TreeItem({ icon: Icon, label, selected, onClick }: TreeItemProps) {
  return (
    <button type="button" className={`${styles.row} ${selected ? styles.selected : ''}`} onClick={onClick}>
      <Icon size={16} className={styles.icon} aria-hidden />
      <span className={styles.name}>{label}</span>
    </button>
  )
}

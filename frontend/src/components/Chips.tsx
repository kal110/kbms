import { Tag } from 'lucide-react'
import type { AcademicTag } from '../api/types'
import styles from './Chips.module.css'

/** Document type chip: white pill with a tag icon, e.g. "SYL · Course syllabus". */
export function TypeChip({ label }: { label: string }) {
  return (
    <span className={styles.type}>
      <Tag size={12} aria-hidden className={styles.typeIcon} />
      {label}
    </span>
  )
}

/** Period tag chip a document type requires: Term, Year or None. */
export function TagChip({ tag, label }: { tag: AcademicTag; label?: string }) {
  const tone = tag === 'Term' ? styles.term : tag === 'Year' ? styles.year : styles.none
  return <span className={`${styles.tag} ${tone}`}>{label ?? (tag === 'None' ? 'No period tag' : `${tag} tag`)}</span>
}

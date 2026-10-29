import { Calendar, FileText, Folder as FolderIcon, Info } from 'lucide-react'
import { getFolderChildren } from '../../api/library'
import type { DocumentGroup, FolderGroups } from '../../api/types'
import { useApi } from '../../api/useApi'
import { ErrorState, Loading } from '../../components/AsyncState'
import { Badge } from '../../components/Badge'
import { TypeChip } from '../../components/Chips'
import { plural } from './folderGroups'
import type { GroupYear } from './folderGroups'
import { stateLabel, stateTone } from './libraryUi'
import styles from './AcademicFoldersPage.module.css'

/** Documents listed per type folder (the API's largest page). */
const GROUP_PAGE_SIZE = 100

interface FolderGroupsViewProps {
  folderId: string
  data: FolderGroups | null
  error: unknown
  onRetry: () => void
  years: GroupYear[]
  /** The year folder open, and the type folder open inside it. */
  year?: GroupYear
  type?: DocumentGroup
  /** '' = every term. */
  termId: string
  termLabel: string
  /** Who the folders below belong to: "staff member" or "faculty member". */
  memberWord: string
  isAdmin: boolean
  onOpenYear: (yearKey: string) => void
  onOpenType: (yearKey: string, typeId: string) => void
  onOpenDocument: (documentId: string) => void
}

/**
 * A folder's documents as year folders, each holding a folder per type, across every faculty or
 * staff folder below it. These folders aren't stored: each document stays filed in its submitter's folder.
 */
export function FolderGroupsView(props: FolderGroupsViewProps) {
  const { folderId, data, error, onRetry, years, year, type, termId, termLabel, memberWord, isAdmin } = props

  if (error) return <ErrorState error={error} onRetry={onRetry} compact />
  if (!data) return <Loading label="Grouping documents…" compact />

  const tagged = termId ? ` tagged ${termLabel}` : ''
  let rows
  let footer: string
  if (year && type) {
    rows = <TypeDocuments folderId={folderId} termId={termId} year={year} type={type} onOpen={props.onOpenDocument} />
    footer = `${plural(type.documents, 'document')}${tagged} from ${plural(type.owners, memberWord)}.`
    if (type.documents > GROUP_PAGE_SIZE) footer += ` Showing the first ${GROUP_PAGE_SIZE}; pick a term or search to narrow them down.`
  } else if (year) {
    rows = year.groups.map((g) => (
      <button
        key={g.type.id}
        type="button"
        className={`${styles.docRow} ${styles.folderRow}`}
        onClick={() => props.onOpenType(year.key, g.type.id)}
      >
        <span className={styles.docCell}>
          <FolderIcon size={16} style={{ color: 'var(--primary)' }} aria-hidden />
          <span className={styles.docText}>
            <strong>{g.type.name}</strong>
            <span>
              Document type · {plural(g.documents, 'document')} from {plural(g.owners, memberWord)}
            </span>
          </span>
        </span>
        <span>
          <TypeChip label={`${g.type.name} · ${g.type.code}`} />
        </span>
        <span className={styles.subtle}>—</span>
        <span />
      </button>
    ))
    footer = `${plural(year.groups.length, 'type folder')} with ${plural(year.documents, 'document')}${tagged}.`
  } else {
    rows = years.map((y) => (
      <button key={y.key} type="button" className={`${styles.docRow} ${styles.folderRow}`} onClick={() => props.onOpenYear(y.key)}>
        <span className={styles.docCell}>
          <Calendar size={16} style={{ color: 'var(--purple-accent)' }} aria-hidden />
          <span className={styles.docText}>
            <strong>{y.name}</strong>
            <span>
              {y.id ? 'Academic year' : 'Types without a year or term'} · {plural(y.groups.length, 'type')} ·{' '}
              {plural(y.documents, 'document')}
            </span>
          </span>
        </span>
        <span className={styles.subtle}>—</span>
        <span className={styles.subtle}>—</span>
        <span />
      </button>
    ))
    footer = `${plural(years.length, 'year folder')} with ${plural(data.total, 'document')}${tagged} from this folder and below.`
  }

  return (
    <>
      {rows}
      {!year && data.total === 0 && (
        <div className={styles.empty}>
          {isAdmin
            ? 'Documents are hidden: admins can repair access but not view content.'
            : termId
              ? `No documents tagged ${termLabel} here or in the folders below.`
              : 'No documents in this folder or below yet.'}
        </div>
      )}
      <div className={styles.docFooter}>
        <Info size={14} aria-hidden />
        <span>{footer} Only the view changes: each document stays in its submitter&apos;s folder.</span>
      </div>
    </>
  )
}

function TypeDocuments({
  folderId,
  termId,
  year,
  type,
  onOpen,
}: {
  folderId: string
  termId: string
  year: GroupYear
  type: DocumentGroup
  onOpen: (documentId: string) => void
}) {
  const docs = useApi(
    (signal) =>
      getFolderChildren(
        folderId,
        { includeSubfolders: true, typeId: type.type.id, yearId: year.id ?? undefined, termId: termId || undefined, pageSize: GROUP_PAGE_SIZE },
        signal,
      ),
    [folderId, termId, type.type.id, year.id],
  )

  if (docs.error) return <ErrorState error={docs.error} onRetry={docs.reload} compact />
  if (!docs.data) return <Loading label="Loading documents…" compact />

  // The list can't ask for "no year", so the untagged folder filters by type and drops any dated
  // documents of a type whose tag changed later.
  const items = year.id ? docs.data.documents.items : docs.data.documents.items.filter((d) => !d.period.academicYearId)
  return items.map((d) => (
    <div key={d.id} className={`${styles.docRow} ${styles.docBody}`} onClick={() => onOpen(d.id)}>
      <span className={styles.docCell}>
        <FileText size={16} className={styles.fileIcon} aria-hidden />
        <span className={styles.docText}>
          <strong>{d.title}</strong>
          <span>
            {d.owner.displayName}
            {d.period.academicTermShortName ? ` · ${d.period.academicTermShortName}` : ''}
            {' · '}
            {d.currentVersion ? `${d.currentVersion.fileName} · v${d.currentVersion.number}` : 'No file yet'}
          </span>
        </span>
      </span>
      <span>
        <TypeChip label={`${d.type.name} · ${d.type.code}`} />
      </span>
      <span className={styles.number}>{d.documentNumber ?? '—'}</span>
      <span>
        <Badge tone={stateTone[d.state]}>{stateLabel[d.state]}</Badge>
      </span>
    </div>
  ))
}

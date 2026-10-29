import { useMemo, useState } from 'react'
import { Inbox, Plus } from 'lucide-react'
import { getMyFolders } from '../../api/library'
import { listSubmissions } from '../../api/workflow'
import type { SubmissionRow } from '../../api/types'
import { useApi } from '../../api/useApi'
import { navFor } from '../../app/navigation'
import { useMe } from '../../auth/SessionContext'
import { ErrorState, Loading } from '../../components/AsyncState'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { PageHeader } from '../../components/PageHeader'
import { shortDate } from '../../lib/format'
import { NewSubmissionModal } from './NewSubmissionModal'
import { SubmissionDetail } from './SubmissionDetail'
import { sortSubmissions, statusMeta, termShort } from './submissionUi'
import styles from './MySubmissionsPage.module.css'

type Filter = 'all' | 'draft' | 'waiting' | 'revision' | 'approved'

const filterTests: Record<Exclude<Filter, 'all'>, (s: SubmissionRow) => boolean> = {
  draft: (s) => s.status === 'Draft',
  waiting: (s) => s.status === 'Submitted' || s.status === 'UnderReview' || s.status === 'Resubmitted',
  revision: (s) => s.status === 'RevisionRequested',
  approved: (s) => s.status === 'Approved' && s.term.status === 'Active',
}

function rowDetail(s: SubmissionRow) {
  // A per-course submission names its course: "Course syllabus · CS 301".
  const type = s.course ? `${s.submissionType.name} · ${s.course.code}` : s.submissionType.name
  if (s.status === 'Draft') {
    const missing = s.missingRequired ?? 0
    return `${s.number} · ${type} · ${
      missing ? `${missing} required ${missing === 1 ? 'document' : 'documents'} missing` : 'all required documents attached'
    }`
  }
  if (s.status === 'Approved') return `${s.number} · ${type} · published ${shortDate(s.finalizedAt ?? s.updatedAt)}`
  if (s.status === 'Rejected') return `${s.number} · ${type} · rejected ${shortDate(s.finalizedAt ?? s.updatedAt)} with explanation`
  return `${s.number} · ${type} · updated ${shortDate(s.updatedAt)}`
}

export function MySubmissionsPage() {
  const me = useMe()
  const list = useApi((signal) => listSubmissions({ scope: 'mine', pageSize: 100 }, signal), [])
  // The person's own faculty/staff folder, where every attached file is filed.
  const folders = useApi((signal) => getMyFolders(signal), [])
  const mine = useMemo(() => sortSubmissions(list.data?.items ?? []), [list.data])
  const [filter, setFilter] = useState<Filter>('all')
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)

  const visible = filter === 'all' ? mine : mine.filter(filterTests[filter])
  const selected = visible.find((s) => s.id === selectedId) ?? visible[0]
  const folderName = folders.data?.[0]?.name
  const section = navFor(me).find((s) => s.items.some((item) => item.to === '/submissions'))?.label ?? 'Academic workflow'

  const stats: { key: Exclude<Filter, 'all'>; label: string; tone: string }[] = [
    { key: 'draft', label: 'Drafts', tone: styles.valueDefault },
    { key: 'waiting', label: 'Waiting for review', tone: styles.valueInfo },
    { key: 'revision', label: 'Needs revision', tone: styles.valueWarning },
    { key: 'approved', label: 'Approved this term', tone: styles.valueSuccess },
  ]

  return (
    <>
      <PageHeader
        breadcrumb={`${section}  /  My submissions`}
        title="My submissions"
        actions={
          <Button variant="primary" icon={Plus} onClick={() => setCreating(true)}>
            New submission
          </Button>
        }
      />

      {list.error && !list.data ? (
        <ErrorState error={list.error} onRetry={list.reload} />
      ) : !list.data ? (
        <Loading label="Loading your submissions…" />
      ) : (
        <>
          <div className={styles.stats}>
            {stats.map((stat) => {
              const active = filter === stat.key
              return (
                <button
                  key={stat.key}
                  type="button"
                  aria-pressed={active}
                  className={`${styles.stat} ${active ? styles.statActive : ''}`}
                  onClick={() => setFilter(active ? 'all' : stat.key)}
                >
                  <span className={styles.statLabel}>{stat.label}</span>
                  <span className={`${styles.statValue} ${stat.tone}`}>{mine.filter(filterTests[stat.key]).length}</span>
                </button>
              )
            })}
          </div>

          <div className={styles.body}>
            <section className={styles.list} aria-label="Submissions">
              <div className={styles.headRow}>
                <span className={styles.colMain}>Submission</span>
                <span className={styles.colTerm}>Term</span>
                <span className={styles.colStatus}>Status</span>
              </div>
              {filter !== 'all' && (
                <div className={styles.filterNote}>
                  Showing {stats.find((s) => s.key === filter)?.label.toLowerCase()} · {visible.length} of {mine.length}
                  <button type="button" className={styles.linkButton} onClick={() => setFilter('all')}>
                    Show all
                  </button>
                </div>
              )}
              {visible.map((s) => {
                const meta = statusMeta[s.status]
                return (
                  <button
                    key={s.id}
                    type="button"
                    className={`${styles.row} ${s.id === selected?.id ? styles.rowSelected : ''}`}
                    aria-current={s.id === selected?.id}
                    onClick={() => setSelectedId(s.id)}
                  >
                    <span className={styles.colMain}>
                      <span className={styles.rowTitle}>{s.title}</span>
                      <span className={styles.rowMeta}>{rowDetail(s)}</span>
                    </span>
                    <span className={`${styles.colTerm} ${styles.rowTerm}`}>{termShort(s.term)}</span>
                    <span className={styles.colStatus}>
                      <Badge tone={meta.tone}>{meta.label}</Badge>
                    </span>
                  </button>
                )
              })}
              {visible.length === 0 && (
                <div className={styles.empty}>
                  <Inbox size={22} aria-hidden />
                  <span className={styles.emptyTitle}>{mine.length ? 'Nothing matches this filter' : 'No submissions yet'}</span>
                  <span>
                    {mine.length
                      ? 'Pick another card above, or show all submissions.'
                      : 'Start one with New submission. The checklist comes from your requirement set.'}
                  </span>
                </div>
              )}
            </section>

            {selected && (
              <SubmissionDetail
                key={selected.id}
                submissionId={selected.id}
                folderName={folderName}
                onChanged={list.reload}
                onDeleted={() => {
                  setSelectedId(null)
                  list.reload()
                }}
              />
            )}
          </div>
        </>
      )}

      {creating && (
        <NewSubmissionModal
          folderName={folderName}
          onClose={() => setCreating(false)}
          onCreated={(id) => {
            setFilter('all')
            setSelectedId(id)
            setCreating(false)
            list.reload()
          }}
        />
      )}
    </>
  )
}

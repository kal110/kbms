import { useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { ArrowRight, ChevronLeft, ChevronRight, GitCompare, Inbox, ListFilter, ShieldAlert, ShieldCheck, UserRoundCog } from 'lucide-react'
import { Link, useSearchParams } from 'react-router'
import { ApiError } from '../../api/client'
import { compareVersions } from '../../api/library'
import { searchPeople } from '../../api/org'
import type { MeDto, PersonDto, ReviewQueueItem, SubmissionDetail, TimelineEntry, VersionComparison } from '../../api/types'
import { useAction, useApi } from '../../api/useApi'
import {
  approveSubmission,
  getPendingReviews,
  getSubmission,
  getTimeline,
  openReview,
  reassignReview,
  rejectSubmission,
  requestRevision,
} from '../../api/workflow'
import { can } from '../../app/navigation'
import { useMe } from '../../auth/SessionContext'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { Modal } from '../../components/Modal'
import { shortDate, termShortLabel, timeOfDay } from '../../lib/format'
import { DocumentCanvas } from './DocumentCanvas'
import {
  attachmentsOf,
  isPending,
  lastDecisionBy,
  loadReviewHistory,
  reviewScope,
  shortName,
  statusMeta,
  waitingLabel,
} from './reviewModel'
import type { DecidedItem, Decision, DecisionKind, ReviewAttachment } from './reviewModel'
import styles from './ReviewWorkspacePage.module.css'

type Sort = 'oldest' | 'newest'

/** Which allowedActions entry each decision needs. */
const decisionAction: Record<DecisionKind, string> = { approve: 'approve', revision: 'requestRevision', reject: 'reject' }

const decisionOptions: { kind: DecisionKind; title: string; hint: (detail: SubmissionDetail, main?: ReviewAttachment) => string }[] = [
  {
    kind: 'approve',
    title: 'Approve',
    hint: (detail, main) => {
      const next = nextStage(detail)
      if (next) return `Sends it to ${next.rule === 'CollegeDean' ? 'the College Dean' : (next.reviewer?.name ?? 'the next reviewer')} for stage ${next.order}`
      return main ? `Publishes ${main.label} v${main.version} to the knowledge base` : 'Publishes it to the knowledge base'
    },
  },
  { kind: 'revision', title: 'Request revision', hint: () => 'Comment required' },
  { kind: 'reject', title: 'Reject', hint: () => 'Explanation required · never published' },
]

const nextStage = (detail: SubmissionDetail) =>
  detail.stages.find((s) => s.order > detail.currentStage && s.state !== 'Skipped' && s.state !== 'Done')

const termLabelOf = (term: SubmissionDetail['term']) => termShortLabel({ shortName: term.shortName, academicYearName: term.yearName })

export function ReviewWorkspacePage() {
  const me = useMe()
  const [params, setParams] = useSearchParams()
  const queue = useApi((signal) => getPendingReviews(1, 100, signal), [])
  const history = useApi((signal) => loadReviewHistory(me.id, signal), [me.id])

  const [typeFilter, setTypeFilter] = useState('all')
  const [sort, setSort] = useState<Sort>('oldest')
  const [filterOpen, setFilterOpen] = useState(false)
  const filterRef = useRef<HTMLDivElement>(null)

  const requested = params.get('id')
  const [selectedId, setSelectedId] = useState<string | undefined>(requested ?? undefined)

  useEffect(() => {
    if (!filterOpen) return
    const onDown = (e: MouseEvent) => {
      if (!filterRef.current?.contains(e.target as Node)) setFilterOpen(false)
    }
    document.addEventListener('mousedown', onDown)
    return () => document.removeEventListener('mousedown', onDown)
  }, [filterOpen])

  const items = useMemo(() => queue.data?.items ?? [], [queue.data])
  const pending = items
    .filter((i) => typeFilter === 'all' || i.submissionType.id === typeFilter)
    .sort((a, b) => (sort === 'oldest' ? b.waitingHours - a.waitingHours : a.waitingHours - b.waitingHours))
  const decided = (history.data?.decidedToday ?? []).filter((d) => !items.some((i) => i.id === d.row.id))
  const usedTypes = [...new Map(items.map((i) => [i.submissionType.id, i.submissionType])).values()]
  const overdue = items.filter((i) => i.overdue).length
  const scope = reviewScope(me)

  // Someone who oversees without reviewing (the VPAF) opens a submission from the dashboard, read-only.
  if (requested && !can(me, 'canReview')) {
    const none = () => undefined
    return (
      <SelectedReview
        key={requested}
        id={requested}
        me={me}
        section="Dashboard"
        queueColumn={<ReadOnlyColumn />}
        onOpened={none}
        onDecided={none}
        onReassigned={none}
        hasNext={false}
        onNext={none}
      />
    )
  }

  if (queue.error || !queue.data) {
    return (
      <>
        <header className={styles.topbar}>
          <div className={styles.titleBlock}>
            <span className={styles.breadcrumb}>Review queue</span>
            <h1 className={styles.title}>Review queue</h1>
          </div>
        </header>
        {queue.error ? <ErrorState error={queue.error} onRetry={queue.reload} /> : <Loading label="Loading your queue…" />}
      </>
    )
  }

  const currentId = selectedId ?? pending[0]?.id ?? decided[0]?.row.id
  if (!currentId) {
    return (
      <>
        <header className={styles.topbar}>
          <div className={styles.titleBlock}>
            <span className={styles.breadcrumb}>Review queue</span>
            <h1 className={styles.title}>Nothing assigned to you</h1>
          </div>
        </header>
        <div className={styles.emptyPage}>
          <Inbox size={24} aria-hidden />
          <span>Submissions waiting for your review show up here, longest-waiting first.</span>
        </div>
      </>
    )
  }

  const select = (id: string) => {
    setSelectedId(id)
    if (params.has('id')) setParams({}, { replace: true })
  }

  const refresh = () => {
    queue.reload()
    history.reload()
  }

  const queueColumn = (
    <section className={styles.queue} aria-label="Review queue">
      <div className={styles.queueHead} ref={filterRef}>
        <div className={styles.queueTitleRow}>
          <span className={styles.queueTitle}>Pending ({pending.length})</span>
          <button
            type="button"
            className={styles.iconButton}
            aria-label="Filter and sort"
            aria-expanded={filterOpen}
            onClick={() => setFilterOpen((o) => !o)}
          >
            <ListFilter size={16} />
          </button>
        </div>
        <span className={styles.queueSub}>
          {scope} · {typeFilter === 'all' ? 'All types' : (usedTypes.find((t) => t.id === typeFilter)?.name ?? 'All types')} ·{' '}
          {sort === 'oldest' ? 'Oldest first' : 'Newest first'}
        </span>
        {filterOpen && (
          <div className={styles.popover}>
            <label className={styles.popField}>
              <span>Type</span>
              <select className="field" value={typeFilter} onChange={(e) => setTypeFilter(e.target.value)}>
                <option value="all">All types</option>
                {usedTypes.map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.name}
                  </option>
                ))}
              </select>
            </label>
            <label className={styles.popField}>
              <span>Sort</span>
              <select className="field" value={sort} onChange={(e) => setSort(e.target.value as Sort)}>
                <option value="oldest">Oldest first</option>
                <option value="newest">Newest first</option>
              </select>
            </label>
          </div>
        )}
      </div>

      <div className={styles.queueList}>
        {pending.map((item) => (
          <QueueItem
            key={item.id}
            title={item.title}
            sub={queueSub(item)}
            overdue={item.overdue}
            selected={item.id === currentId}
            onSelect={() => select(item.id)}
          />
        ))}
        {pending.length === 0 && <span className={styles.queueEmpty}>No pending submissions match.</span>}
        {decided.length > 0 && (
          <>
            <span className={styles.queueGroup}>Decided today ({decided.length})</span>
            {decided.map((d) => (
              <QueueItem
                key={d.row.id}
                title={d.row.title}
                sub={decidedSub(d)}
                overdue={false}
                selected={d.row.id === currentId}
                onSelect={() => select(d.row.id)}
              />
            ))}
          </>
        )}
      </div>

      <div className={styles.workload}>
        <span className={styles.workloadTitle}>Your workload</span>
        <span className={styles.workloadText}>
          {items.length} open · {overdue} overdue
          {history.data?.avgDaysToDecision != null && ` · avg ${history.data.avgDaysToDecision.toFixed(1)} d to decision`}
        </span>
      </div>
    </section>
  )

  const others = pending.filter((i) => i.id !== currentId)

  return (
    <SelectedReview
      key={currentId}
      id={currentId}
      me={me}
      queueItem={items.find((i) => i.id === currentId)}
      queueColumn={queueColumn}
      onOpened={queue.reload}
      onDecided={() => {
        // Keep the decided item on screen to show the result.
        setSelectedId(currentId)
        refresh()
      }}
      onReassigned={refresh}
      hasNext={others.length > 0}
      onNext={() => {
        if (others[0]) select(others[0].id)
      }}
    />
  )
}

/** In place of the queue for someone without reviews: they can read the submission and its files. */
function ReadOnlyColumn() {
  return (
    <section className={styles.queue} aria-label="View only">
      <div className={styles.queueHead}>
        <span className={styles.queueTitle}>View only</span>
        <span className={styles.queueSub}>You can read this submission and open its files. Its reviewers decide on it.</span>
      </div>
      <div className={styles.queueList}>
        <Link to="/dashboard" className={styles.backLink}>
          <ChevronLeft size={14} aria-hidden /> Back to the dashboard
        </Link>
      </div>
    </section>
  )
}

function queueSub(item: ReviewQueueItem) {
  const age = waitingLabel(item.waitingHours, item.waitingDays)
  if (item.stageNote) return `${item.unit.code} · ${item.stageNote} · ${age}`
  return `${shortName(item.submitter.name)} · ${statusMeta[item.status].label} · ${age}${item.overdue ? ' · overdue' : ''}`
}

function decidedSub({ row, decision }: DecidedItem) {
  const verb = decision.kind === 'approve' ? 'Approved' : decision.kind === 'revision' ? 'Revision requested' : 'Rejected'
  return `${shortName(row.submitter.name)} · ${verb} · ${timeOfDay(decision.at)}`
}

function QueueItem({
  title,
  sub,
  overdue,
  selected,
  onSelect,
}: {
  title: string
  sub: string
  overdue: boolean
  selected: boolean
  onSelect: () => void
}) {
  return (
    <button
      type="button"
      className={`${styles.queueItem} ${selected ? styles.queueItemSelected : ''}`}
      aria-current={selected}
      onClick={onSelect}
    >
      <span className={styles.queueItemTitle}>{title}</span>
      <span className={overdue ? styles.queueItemSubOverdue : styles.queueItemSub}>{sub}</span>
    </button>
  )
}

interface SelectedReviewProps {
  id: string
  me: MeDto
  /** The breadcrumb's first part: where the submission was opened from. */
  section?: string
  queueItem?: ReviewQueueItem
  queueColumn: ReactNode
  onOpened: () => void
  onDecided: () => void
  onReassigned: () => void
  hasNext: boolean
  onNext: () => void
}

/** Header, document pane and decision panel for one submission, sharing its detail and timeline. */
function SelectedReview({ id, me, section = 'Review queue', queueItem, queueColumn, onOpened, onDecided, onReassigned, hasNext, onNext }: SelectedReviewProps) {
  const detail = useApi((signal) => getSubmission(id, signal), [id])
  const timeline = useApi((signal) => getTimeline(id, signal), [id])
  const opened = useRef(false)
  const openedRef = useRef(onOpened)

  useEffect(() => {
    openedRef.current = onOpened
  })

  // Opening a Submitted or Resubmitted item records the move to Under review (once per visit).
  const canOpen = detail.data?.allowedActions.includes('open') ?? false
  const setDetail = detail.setData
  const reloadTimeline = timeline.reload
  useEffect(() => {
    if (!canOpen || opened.current) return
    opened.current = true
    openReview(id).then(
      (next) => {
        setDetail(next)
        reloadTimeline()
        openedRef.current()
      },
      () => undefined, // Someone else opened it first; deciding still works.
    )
  }, [canOpen, id, setDetail, reloadTimeline])

  const d = detail.data
  const title = d?.title ?? queueItem?.title ?? 'Submission'
  const stage = d?.currentStage ?? queueItem?.stage ?? 1
  const stageLabel = queueItem?.stageLabel ?? (stage > 1 ? `Stage ${stage}` : null)
  const showStage = stageLabel !== null && stageLabel !== 'Stage 1'
  const assignedToYou = d ? d.assignment?.reviewer.id === me.id : Boolean(queueItem)

  return (
    <>
      <header className={styles.topbar}>
        <div className={styles.titleBlock}>
          <span className={styles.breadcrumb}>
            {section}  /  {d?.number ?? queueItem?.number ?? ''}
          </span>
          <div className={styles.titleRow}>
            <h1 className={styles.title}>{title}</h1>
            {d && <Badge tone={statusMeta[d.status].tone}>{statusMeta[d.status].label}</Badge>}
            {showStage && <Badge tone={stage >= 2 ? 'purple' : 'warning'}>{stageLabel}</Badge>}
            {assignedToYou && d && isPending(d.status) && <Badge tone="neutral">Assigned to you</Badge>}
          </div>
        </div>
        {d && (
          <span className={styles.topbarMeta}>
            {d.submitter.name} · {d.unit.name} · {termLabelOf(d.term)}
          </span>
        )}
      </header>

      <div className={styles.body}>
        {queueColumn}

        {detail.error ? (
          <section className={styles.pane}>
            <ErrorState error={detail.error} onRetry={detail.reload} />
          </section>
        ) : !d ? (
          <section className={styles.pane}>
            <Loading />
          </section>
        ) : (
          <ReviewPane attachments={attachmentsOf(d)} lastReviewedAt={queueItem?.lastReviewedAt ?? null} />
        )}

        {d ? (
          <DecisionPanel
            detail={d}
            setDetail={detail.setData}
            reloadDetail={detail.reload}
            timeline={timeline.data ?? []}
            me={me}
            queueItem={queueItem}
            onDecided={() => {
              timeline.reload()
              onDecided()
            }}
            onReassigned={(next) => {
              detail.setData(next)
              timeline.reload()
              onReassigned()
            }}
            hasNext={hasNext}
            onNext={onNext}
          />
        ) : (
          <aside className={styles.decisionPanel} aria-label="Decision" />
        )}
      </div>
    </>
  )
}

const changeLabels: Record<string, string> = {
  sha256: 'content',
  fileName: 'file name',
  sizeBytes: 'size',
  pageCount: 'page count',
  contentType: 'file type',
  changeDescription: 'change note',
}

function changeSummary(comparison: VersionComparison) {
  if (comparison.sameContent) return 'The file content is the same.'
  const fields = comparison.changes.map((c) => changeLabels[c.field]).filter(Boolean)
  return fields.length ? `Changed: ${fields.join(', ')}.` : 'The file changed.'
}

function ReviewPane({ attachments, lastReviewedAt }: { attachments: ReviewAttachment[]; lastReviewedAt: string | null }) {
  const changed = attachments.find((a) => a.previousVersion !== null)
  const [attachmentIndex, setAttachmentIndex] = useState(() => Math.max(0, changed ? attachments.indexOf(changed) : 0))
  const [page, setPage] = useState(1)
  const [showPrevious, setShowPrevious] = useState(false)
  const comparison = useApi(
    (signal) =>
      changed ? compareVersions(changed.documentId, changed.previousVersion!, changed.version, signal) : Promise.resolve(null),
    [changed?.documentId, changed?.previousVersion, changed?.version],
  )

  if (attachments.length === 0) {
    return (
      <section className={styles.pane} aria-label="Document under review">
        <div className={styles.canvas}>
          <span className={styles.queueEmpty}>No files are attached to this submission.</span>
        </div>
      </section>
    )
  }

  const attachment = attachments[attachmentIndex] ?? attachments[0]
  const previous = showPrevious && attachment === changed ? comparison.data?.from : undefined
  const shown = previous
    ? {
        versionId: previous.id,
        version: previous.number,
        fileName: previous.fileName,
        contentType: previous.contentType,
        pageCount: previous.pageCount,
        sizeBytes: previous.sizeBytes,
      }
    : attachment
  const pageCount = shown.pageCount
  const pageable = shown.contentType === 'application/pdf' && pageCount !== null && pageCount > 0

  const openAttachment = (index: number) => {
    setAttachmentIndex(index)
    setPage(1)
    setShowPrevious(false)
  }

  const togglePrevious = () => {
    if (!changed) return
    setAttachmentIndex(attachments.indexOf(changed))
    setShowPrevious((p) => !p)
    setPage(1)
  }

  return (
    <section className={styles.pane} aria-label="Document under review">
      {changed && (
        <div className={styles.banner}>
          <GitCompare size={18} aria-hidden className={styles.bannerIcon} />
          <span className={styles.bannerText}>
            Reviewing {changed.label} v{changed.version} (sha256 {changed.sha.slice(0, 8)}). Your last review was on v
            {changed.previousVersion}
            {lastReviewedAt && ` (${shortDate(lastReviewedAt)})`}. {comparison.data && changeSummary(comparison.data)}
          </span>
          {comparison.data && (
            <button type="button" className={styles.bannerAction} onClick={togglePrevious}>
              {showPrevious ? `Show v${changed.version}` : `Show v${changed.previousVersion}`}
            </button>
          )}
        </div>
      )}

      <div className={styles.toolbar}>
        <div className={styles.attachmentTabs} role="tablist">
          {attachments.map((a, i) => (
            <button
              key={a.key}
              type="button"
              role="tab"
              aria-selected={i === attachmentIndex}
              className={`${styles.attachmentTab} ${i === attachmentIndex ? styles.attachmentTabActive : ''}`}
              onClick={() => openAttachment(i)}
            >
              {a.label} v{a === attachment && previous ? previous.number : a.version}
            </button>
          ))}
        </div>
        {pageable && (
          <div className={styles.pager}>
            {previous && <Badge tone="warning">Previous version</Badge>}
            <button
              type="button"
              className={styles.iconButton}
              aria-label="Previous page"
              disabled={page <= 1}
              onClick={() => setPage((p) => p - 1)}
            >
              <ChevronLeft size={16} />
            </button>
            <span className={styles.pagerText}>
              Page {page} / {pageCount}
            </span>
            <button
              type="button"
              className={styles.iconButton}
              aria-label="Next page"
              disabled={page >= pageCount}
              onClick={() => setPage((p) => p + 1)}
            >
              <ChevronRight size={16} />
            </button>
          </div>
        )}
      </div>

      <DocumentCanvas
        key={shown.versionId}
        documentId={attachment.documentId}
        versionId={shown.versionId}
        version={shown.version}
        label={attachment.label}
        fileName={shown.fileName}
        contentType={shown.contentType}
        sizeBytes={shown.sizeBytes}
        page={page}
      />
    </section>
  )
}

interface DecisionPanelProps {
  detail: SubmissionDetail
  setDetail: (detail: SubmissionDetail) => void
  reloadDetail: () => void
  timeline: TimelineEntry[]
  me: MeDto
  queueItem?: ReviewQueueItem
  onDecided: () => void
  onReassigned: (detail: SubmissionDetail) => void
  hasNext: boolean
  onNext: () => void
}

function DecisionPanel({ detail, setDetail, reloadDetail, timeline, me, queueItem, onDecided, onReassigned, hasNext, onNext }: DecisionPanelProps) {
  const options = decisionOptions.filter((o) => detail.allowedActions.includes(decisionAction[o.kind]))
  const [kind, setKind] = useState<DecisionKind>(options[0]?.kind ?? 'approve')
  const [comment, setComment] = useState('')
  const [reassigning, setReassigning] = useState(false)
  const attachments = attachmentsOf(detail)
  const main = attachments[0]
  const isStaff = detail.unit.kind === 'Department'
  const needsComment = kind !== 'approve'
  const myDecision = lastDecisionBy(timeline, me.id)

  const decide = useAction(async () => {
    const body = { comments: comment.trim() || null, reviewedVersionIds: attachments.map((a) => a.versionId) }
    const send = kind === 'approve' ? approveSubmission : kind === 'revision' ? requestRevision : rejectSubmission
    return send(detail.id, detail.version, body)
  })
  const stale = decide.error instanceof ApiError && decide.error.isStale

  const submit = async () => {
    const next = await decide.run()
    if (!next) return
    setDetail(next)
    setComment('')
    onDecided()
  }

  const unitLabel =
    detail.unit.kind === 'Program'
      ? [detail.unit.name, detail.unit.collegeCode].filter(Boolean).join(' · ')
      : `${detail.unit.name} · non-academic department`
  const resubmittedAt = queueItem?.resubmittedAt ?? null

  const rows: [string, string][] = [
    [isStaff ? 'Staff' : 'Faculty', detail.submitter.name],
    [isStaff ? 'Department' : 'Program', unitLabel],
    ['Type', [detail.submissionType.name, main?.typeCode].filter(Boolean).join(' · ')],
    ...(detail.course ? [['Course', `${detail.course.code} · ${detail.course.title}`] as [string, string]] : []),
    [isStaff ? 'Period' : 'Term', termLabelOf(detail.term)],
    ['Submitted', `${shortDate(detail.submittedAt)}${resubmittedAt ? ` · resubmitted ${shortDate(resubmittedAt)}` : ''}`],
  ]

  return (
    <aside className={styles.decisionPanel} aria-label="Decision">
      <div className={styles.metadata}>
        <span className="section-label">Submission</span>
        {rows.map(([label, value]) => (
          <div key={label} className={styles.metaRow}>
            <span className={styles.metaLabel}>{label}</span>
            <span className={styles.metaValue}>{value}</span>
          </div>
        ))}
        {queueItem?.reason === 'Fallback' && queueItem.stageNote && <span className={styles.metaNote}>{queueItem.stageNote}.</span>}
        {detail.allowedActions.includes('reassign') && (
          <button type="button" className={styles.linkButton} onClick={() => setReassigning(true)}>
            <UserRoundCog size={14} aria-hidden />
            Reassign reviewer…
          </button>
        )}
      </div>

      {attachments.length > 0 && options.length > 0 && (
        <div className={styles.versionCheck}>
          {stale ? (
            <div className={`${styles.versionBox} ${styles.versionBoxStale}`}>
              <ShieldAlert size={18} aria-hidden className={styles.versionIconStale} />
              <span className={styles.versionText}>
                <span className={styles.versionTitle}>The files changed</span>
                <span className={styles.versionDetail}>
                  A newer version was attached since you opened this.{' '}
                  <button
                    type="button"
                    className={styles.pageLink}
                    onClick={() => {
                      decide.clearError()
                      reloadDetail()
                    }}
                  >
                    Load the current versions
                  </button>
                </span>
              </span>
            </div>
          ) : (
            <div className={styles.versionBox}>
              <ShieldCheck size={18} aria-hidden className={styles.versionIcon} />
              <span className={styles.versionText}>
                <span className={styles.versionTitle}>Version check passed</span>
                <span className={styles.versionDetail}>
                  The attachments are still the versions on screen: {attachments.map((a) => `${a.label} v${a.version}`).join(', ')}.
                </span>
              </span>
            </div>
          )}
        </div>
      )}

      {options.length > 0 ? (
        <div className={styles.decision}>
          <span className="section-label">Decision</span>
          {options.map((option) => (
            <label key={option.kind} className={`${styles.option} ${kind === option.kind ? styles.optionActive : ''}`}>
              <input
                type="radio"
                name="decision"
                className="visually-hidden"
                checked={kind === option.kind}
                onChange={() => setKind(option.kind)}
              />
              <span className={styles.radio} aria-hidden />
              <span className={styles.optionText}>
                <span className={styles.optionTitle}>{option.title}</span>
                <span className={styles.optionHint}>{option.hint(detail, main)}</span>
              </span>
            </label>
          ))}
          <textarea
            className={styles.commentBox}
            value={comment}
            onChange={(e) => setComment(e.target.value)}
            placeholder={
              kind === 'approve'
                ? 'Comment (optional when approving)…'
                : kind === 'revision'
                  ? 'What needs to change? (required)…'
                  : 'Why is it rejected? (required)…'
            }
            aria-label="Comment"
          />
          {!stale && <InlineError error={decide.error} />}
          <div className={styles.decisionActions}>
            <Button
              disabled={decide.pending}
              onClick={() => {
                setKind(options[0].kind)
                setComment('')
                decide.clearError()
              }}
            >
              Cancel
            </Button>
            <Button
              variant={kind === 'approve' ? 'success' : kind === 'revision' ? 'primary' : 'danger'}
              disabled={decide.pending || stale || (needsComment && comment.trim().length === 0)}
              onClick={submit}
            >
              {decide.pending
                ? 'Saving…'
                : kind === 'approve'
                  ? main
                    ? `Approve v${main.version}`
                    : 'Approve'
                  : kind === 'revision'
                    ? 'Request revision'
                    : 'Reject'}
            </Button>
          </div>
        </div>
      ) : myDecision ? (
        <DecisionResult detail={detail} decision={myDecision} main={main} hasNext={hasNext} onNext={onNext} />
      ) : (
        <div className={styles.decision}>
          <span className="section-label">Decision</span>
          <span className={styles.optionHint}>
            {isPending(detail.status)
              ? `Waiting for ${detail.assignment?.reviewer.name ?? 'the assigned reviewer'} at stage ${detail.currentStage}.`
              : `${statusMeta[detail.status].label}. Nothing to decide.`}
          </span>
        </div>
      )}

      <div className={styles.history}>
        <span className="section-label">
          Review history <span className={styles.noCase}>(append-only)</span>
        </span>
        {[...timeline].reverse().map((h, i) => (
          <div key={`${h.at}-${i}`} className={styles.historyItem}>
            <span>
              {shortDate(h.at)} · {h.text}
            </span>
            {h.comment && <span className={styles.historyComment}>“{h.comment}”</span>}
          </div>
        ))}
      </div>

      {reassigning && (
        <ReassignModal
          detail={detail}
          onClose={() => setReassigning(false)}
          onDone={(next) => {
            setReassigning(false)
            onReassigned(next)
          }}
        />
      )}
    </aside>
  )
}

function DecisionResult({
  detail,
  decision,
  main,
  hasNext,
  onNext,
}: {
  detail: SubmissionDetail
  decision: Decision
  main?: ReviewAttachment
  hasNext: boolean
  onNext: () => void
}) {
  const submitter = shortName(detail.submitter.name)
  const notified = `${submitter} has been notified${decision.comment ? ' with your comment' : ''}.`
  const movedOn = decision.kind === 'approve' && isPending(detail.status)
  const assignee = detail.assignment?.reviewer.name

  return (
    <div className={styles.decision}>
      <span className="section-label">Decision</span>
      <div className={`${styles.result} ${styles[`result_${decision.kind}`]}`}>
        <span className={styles.resultTitle}>
          {decision.kind === 'approve' &&
            (movedOn ? `Approved stage ${decision.stage ?? ''}`.trim() : main ? `Approved ${main.label} v${main.version}` : 'Approved')}
          {decision.kind === 'revision' && 'Revision requested'}
          {decision.kind === 'reject' && 'Rejected'}
        </span>
        <span className={styles.resultText}>
          {decision.kind === 'approve' &&
            (movedOn ? `Sent to ${assignee ?? 'the next reviewer'} for stage ${detail.currentStage}. ` : 'Published to the knowledge base. ')}
          {decision.kind === 'reject' && 'It will never be published. '}
          {notified}
        </span>
      </div>
      {hasNext && (
        <div className={styles.decisionActions}>
          <Button variant="primary" iconRight={ArrowRight} onClick={onNext}>
            Next in queue
          </Button>
        </div>
      )}
    </div>
  )
}

function ReassignModal({
  detail,
  onClose,
  onDone,
}: {
  detail: SubmissionDetail
  onClose: () => void
  onDone: (detail: SubmissionDetail) => void
}) {
  const [query, setQuery] = useState('')
  const [debounced, setDebounced] = useState('')
  const [chosen, setChosen] = useState<PersonDto | null>(null)
  const [reason, setReason] = useState('')

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(query.trim()), 250)
    return () => clearTimeout(timer)
  }, [query])

  const people = useApi((signal) => searchPeople(debounced, 1, 8, signal), [debounced])
  const reassign = useAction(() =>
    reassignReview(detail.id, detail.version, { reviewerUserId: chosen!.id, reason: reason.trim() || null }),
  )
  const current = detail.assignment?.reviewer

  return (
    <Modal
      title="Reassign reviewer"
      description={`Stage ${detail.currentStage} of ${detail.number}${current ? ` is with ${current.name}` : ''}.`}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            disabled={!chosen || reassign.pending}
            onClick={async () => {
              const next = await reassign.run()
              if (next) onDone(next)
            }}
          >
            {reassign.pending ? 'Reassigning…' : chosen ? `Reassign to ${chosen.displayName}` : 'Reassign'}
          </Button>
        </>
      }
    >
      <input
        className="field"
        type="search"
        placeholder="Find a person by name or email"
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        aria-label="Find a person"
        autoFocus
      />
      <div className={styles.people}>
        {people.error ? (
          <ErrorState error={people.error} onRetry={people.reload} compact />
        ) : !people.data ? (
          <Loading compact />
        ) : (
          people.data.items
            .filter((p) => p.id !== detail.submitter.id)
            .map((p) => (
              <button
                key={p.id}
                type="button"
                className={`${styles.person} ${chosen?.id === p.id ? styles.personSelected : ''}`}
                aria-pressed={chosen?.id === p.id}
                onClick={() => setChosen(p)}
              >
                <span className={styles.personName}>{p.displayName}</span>
                <span className={styles.personMeta}>{[p.title, p.email].filter(Boolean).join(' · ')}</span>
              </button>
            ))
        )}
      </div>
      <textarea
        className={styles.commentBox}
        value={reason}
        onChange={(e) => setReason(e.target.value)}
        placeholder="Reason (optional)…"
        aria-label="Reason"
      />
      <InlineError error={reassign.error} />
    </Modal>
  )
}

import { useState } from 'react'
import { CircleAlert, CircleCheck, CircleDashed, FileX, Pencil, Upload } from 'lucide-react'
import { Link } from 'react-router'
import { getDocumentTypes } from '../../api/reference'
import type { ChecklistItem, SubmissionDetail as Submission, TimelineEntry } from '../../api/types'
import { useAction, useApi } from '../../api/useApi'
import type { ApiState } from '../../api/useApi'
import {
  deleteDraft,
  getSubmission,
  getTimeline,
  renameSubmission,
  resubmitSubmission,
  submitSubmission,
  withdrawSubmission,
} from '../../api/workflow'
import { useMe } from '../../auth/SessionContext'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { TypeChip } from '../../components/Chips'
import { shortDate } from '../../lib/format'
import { AttachModal } from './AttachModal'
import {
  changedSinceSubmit,
  commentLabel,
  currentStage,
  filedPath,
  isPending,
  lowerFirst,
  missingRequired,
  ownScope,
  periodTagText,
  reviewerLabel,
  ruleOf,
  sharedTag,
  slotState,
  sourceCode,
  statusMeta,
  termShort,
  versionList,
} from './submissionUi'
import type { SlotTone } from './submissionUi'
import styles from './MySubmissionsPage.module.css'

const slotIcon: Record<SlotTone, React.ReactNode> = {
  warn: <CircleAlert size={18} className={styles.iconWarn} aria-hidden />,
  ok: <CircleCheck size={18} className={styles.iconOk} aria-hidden />,
  empty: <CircleDashed size={18} className={styles.iconEmpty} aria-hidden />,
  closed: <FileX size={18} className={styles.iconEmpty} aria-hidden />,
}

const dotClass: Record<string, string> = {
  neutral: styles.dotNeutral,
  info: styles.dotInfo,
  warning: styles.dotWarning,
  success: styles.dotSuccess,
  danger: styles.dotDanger,
  pending: styles.dotPending,
}

interface SubmissionDetailProps {
  submissionId: string
  /** The submitter's own folder name (from /folders/me), when known. */
  folderName?: string
  /** Something changed the submission: refresh the list. */
  onChanged: () => void
  onDeleted: () => void
}

export function SubmissionDetail({ submissionId, folderName, onChanged, onDeleted }: SubmissionDetailProps) {
  const detail = useApi((signal) => getSubmission(submissionId, signal), [submissionId])
  const timeline = useApi((signal) => getTimeline(submissionId, signal), [submissionId])

  if (!detail.data) {
    return (
      <aside className={styles.detail} aria-label="Submission detail">
        {detail.error ? <ErrorState error={detail.error} onRetry={detail.reload} compact /> : <Loading label="Loading submission…" />}
      </aside>
    )
  }

  return (
    <DetailBody
      s={detail.data}
      timeline={timeline}
      folderName={folderName}
      onUpdated={(next) => {
        detail.setData(next)
        timeline.reload()
        onChanged()
      }}
      onDeleted={onDeleted}
    />
  )
}

interface DetailBodyProps {
  s: Submission
  timeline: ApiState<TimelineEntry[]>
  folderName?: string
  onUpdated: (next: Submission) => void
  onDeleted: () => void
}

function DetailBody({ s, timeline, folderName, onUpdated, onDeleted }: DetailBodyProps) {
  const me = useMe()
  const documentTypes = useApi(() => getDocumentTypes(), [])
  const [attachKey, setAttachKey] = useState<string | null>(null)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [renaming, setRenaming] = useState<string | null>(null)

  const can = (action: string) => s.allowedActions.includes(action)
  const meta = statusMeta[s.status]
  const editable = can('attach')
  const missing = missingRequired(s)
  const changed = changedSinceSubmit(s)
  const attachSlot = s.checklist.find((slot) => slot.key === attachKey)
  const tagOf = (slot: ChecklistItem) => documentTypes.data?.find((t) => t.id === slot.documentType?.id)?.tag

  // Files are tagged and filed by their type's period tag: term → year/term folder, year → year folder.
  const tag = sharedTag(s.checklist.map(tagOf))
  const periodText =
    tag === undefined || tag === 'None' ? '' : ` and ${tag === 'mixed' ? 'their term or academic year' : periodTagText(tag, s.term)}`
  const filedText =
    tag && folderName
      ? ` · they are filed ${tag === 'mixed' ? 'under' : 'in'} your folder ${filedPath(s.unit.code, folderName, s.term, tag === 'mixed' ? 'None' : tag)}`
      : ''

  const ownSet = s.requirementSets.find((set) => set.scope === ownScope(s.unit))
  const setter = s.unit.kind === 'Program' ? 'the VPAF' : 'your department'
  const reviewerName = (s.assignment?.reviewer ?? currentStage(s)?.reviewer)?.name

  // Every action answers the updated submission; a stale version (412) asks for a reload.
  const act = useAction(async (run: () => Promise<Submission>) => onUpdated(await run()))
  const remove = useAction(async () => {
    await deleteDraft(s.id, s.version)
    return true
  })
  const rename = useAction((title: string) => renameSubmission(s.id, s.version, title))

  const saveTitle = async () => {
    if (renaming === null) return
    const next = await rename.run(renaming.trim())
    if (next) {
      setRenaming(null)
      onUpdated(next)
    }
  }

  const libraryDocumentId = s.checklist.flatMap((slot) => slot.attachments)[0]?.documentId

  return (
    <aside className={styles.detail} aria-label="Submission detail">
      <div className={styles.detailHead}>
        <span className={styles.detailMeta}>
          {s.number} · {s.submissionType.name}
          {s.course ? ` · ${s.course.code} ${s.course.title}` : ''} · {termShort(s.term)}
        </span>
        {renaming === null ? (
          <h2 className={styles.detailTitle}>
            {s.title}
            {can('edit') && (
              <button type="button" className={styles.titleEdit} aria-label="Rename submission" onClick={() => setRenaming(s.title)}>
                <Pencil size={13} aria-hidden />
              </button>
            )}
          </h2>
        ) : (
          <form
            className={styles.renameForm}
            onSubmit={(e) => {
              e.preventDefault()
              void saveTitle()
            }}
          >
            <input className="field" value={renaming} onChange={(e) => setRenaming(e.target.value)} aria-label="Submission title" autoFocus />
            <Button size="sm" onClick={() => setRenaming(null)}>
              Cancel
            </Button>
            <Button size="sm" variant="primary" type="submit" disabled={rename.pending || !renaming.trim()}>
              Save
            </Button>
          </form>
        )}
        <InlineError error={rename.error} />
        <div className={styles.detailStatus}>
          <Badge tone={meta.tone}>{meta.label}</Badge>
          <span className={styles.detailReviewer}>Reviewer: {reviewerLabel(s)}</span>
        </div>
      </div>

      <div className={styles.section}>
        <span className="section-label">Required documents</span>
        <p className={styles.sectionHint}>
          {ownSet ? `${s.unit.code} requirement set v${ownSet.setVersion}, set by ${setter}` : `${s.submissionType.name} checklist`} · each
          slot is already tagged with a document type, so files you attach get that type{periodText}
          {filedText}
        </p>
        {s.checklist.map((slot) => {
          const state = slotState(s.status, slot)
          const from = sourceCode(slot.source, s.unit, me)
          const content = (
            <>
              {slotIcon[state.tone]}
              <span className={styles.slotText}>
                <span className={styles.slotTitle}>
                  {slot.label} · {lowerFirst(ruleOf(slot))}
                </span>
                <span className={state.tone === 'warn' ? styles.slotNoteWarn : styles.slotNote}>{state.note}</span>
              </span>
              {from && <Badge tone="purple">From {from}</Badge>}
              {slot.documentType && <TypeChip label={slot.documentType.code} />}
              {editable && (
                <span className={styles.slotAction}>
                  <Upload size={13} aria-hidden />
                  {slot.attachments.length ? (slot.attachments.length < slot.maxFiles ? 'Add or update' : 'New version') : 'Attach'}
                </span>
              )}
            </>
          )
          const className = `${styles.slot} ${state.tone === 'warn' ? styles.slotWarn : ''}`
          return editable ? (
            <button key={slot.key} type="button" className={`${className} ${styles.slotButton}`} onClick={() => setAttachKey(slot.key)}>
              {content}
            </button>
          ) : (
            <div key={slot.key} className={className}>
              {content}
            </div>
          )
        })}
      </div>

      {s.comments.length > 0 && (
        <div className={styles.section}>
          <span className="section-label">{s.status === 'Rejected' ? 'Reviewer explanation' : 'Reviewer comments'}</span>
          {s.comments.map((c) => (
            <div key={`${c.at}-${c.action}`} className={styles.comment}>
              <span className={styles.commentHead}>
                {c.author.name} · {shortDate(c.at)} · {commentLabel(c.action, c.stage)}
              </span>
              <span className={styles.commentBody}>{c.body}</span>
            </div>
          ))}
        </div>
      )}

      <div className={styles.section}>
        <span className="section-label">Timeline</span>
        {timeline.error && !timeline.data ? (
          <ErrorState error={timeline.error} onRetry={timeline.reload} compact />
        ) : !timeline.data ? (
          <Loading compact />
        ) : (
          timeline.data.map((t, i) => (
            <div key={i} className={styles.timelineItem}>
              <span className={`${styles.dot} ${dotClass[t.tone] ?? styles.dotNeutral}`} />
              <span className={t.tone === 'pending' ? styles.timelinePending : undefined}>
                {shortDate(t.at)} · {t.text}
              </span>
            </div>
          ))
        )}
      </div>

      <div className={styles.flex} />

      {Boolean(act.error ?? remove.error) && (
        <div className={styles.footerError}>
          <InlineError error={act.error ?? remove.error} />
        </div>
      )}

      <footer className={styles.footer}>
        {s.status === 'Draft' &&
          (confirmDelete ? (
            <>
              <span className={styles.footerNote}>Delete this draft and its uploads?</span>
              <Button onClick={() => setConfirmDelete(false)}>Keep</Button>
              <Button
                variant="danger"
                disabled={remove.pending}
                onClick={async () => {
                  if (await remove.run()) onDeleted()
                }}
              >
                Delete draft
              </Button>
            </>
          ) : (
            <>
              {missing.length > 0 && (
                <span className={styles.footerNote}>
                  {missing.length} required {missing.length === 1 ? 'document' : 'documents'} missing
                </span>
              )}
              {can('deleteDraft') && <Button onClick={() => setConfirmDelete(true)}>Delete draft</Button>}
              {can('submit') && (
                <Button
                  variant="primary"
                  disabled={missing.length > 0 || act.pending}
                  onClick={() => act.run(() => submitSubmission(s.id, s.version))}
                >
                  Submit for review
                </Button>
              )}
            </>
          ))}
        {s.status === 'RevisionRequested' && (
          <>
            {changed.length === 0 && <span className={styles.footerNote}>Upload a corrected file to resubmit</span>}
            {can('withdraw') && (
              <Button disabled={act.pending} onClick={() => act.run(() => withdrawSubmission(s.id, s.version))}>
                Withdraw
              </Button>
            )}
            {can('resubmit') && (
              <Button
                variant="primary"
                disabled={changed.length === 0 || missing.length > 0 || act.pending}
                onClick={() => act.run(() => resubmitSubmission(s.id, s.version))}
              >
                {changed.length ? `Resubmit with ${versionList(changed)}` : 'Resubmit'}
              </Button>
            )}
          </>
        )}
        {isPending(s.status) && (
          <>
            <span className={styles.footerNote}>{reviewerName ? `Waiting for ${reviewerName}` : 'Waiting for a reviewer'}</span>
            {can('withdraw') && (
              <Button disabled={act.pending} onClick={() => act.run(() => withdrawSubmission(s.id, s.version))}>
                Withdraw
              </Button>
            )}
          </>
        )}
        {s.status === 'Approved' && libraryDocumentId && (
          <Link to={`/documents/${libraryDocumentId}`} className={styles.footerLink}>
            Open in library
          </Link>
        )}
        {s.status === 'Rejected' && <span className={styles.footerNote}>Rejected submissions are never published.</span>}
      </footer>

      {attachSlot && (
        <AttachModal
          submission={s}
          slot={attachSlot}
          tag={tagOf(attachSlot)}
          folderName={folderName}
          onClose={() => setAttachKey(null)}
          onAttached={(next) => {
            setAttachKey(null)
            onUpdated(next)
          }}
          onChanged={onUpdated}
        />
      )}
    </aside>
  )
}

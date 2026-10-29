import type {
  MeDto,
  ReviewActionType,
  SubmissionDetail,
  SubmissionRow,
  SubmissionStatus,
  TimelineEntry,
} from '../../api/types'
import { getTimeline, listSubmissions } from '../../api/workflow'
import type { BadgeTone } from '../../components/Badge'
import { todayIso } from '../../lib/format'

// Shapes the review workspace (frame 4) and college overview (frame 15) read from the workflow API.

export const statusMeta: Record<SubmissionStatus, { label: string; tone: BadgeTone }> = {
  Draft: { label: 'Draft', tone: 'neutral' },
  Submitted: { label: 'Submitted', tone: 'info' },
  UnderReview: { label: 'Under review', tone: 'info' },
  RevisionRequested: { label: 'Revision requested', tone: 'warning' },
  Resubmitted: { label: 'Resubmitted', tone: 'info' },
  Approved: { label: 'Approved', tone: 'success' },
  Rejected: { label: 'Rejected', tone: 'danger' },
  Withdrawn: { label: 'Withdrawn', tone: 'neutral' },
}

export const isPending = (status: SubmissionStatus) =>
  status === 'Submitted' || status === 'UnderReview' || status === 'Resubmitted'

/** One file of the submission as the reviewer sees it. */
export interface ReviewAttachment {
  key: string
  label: string
  typeCode: string | null
  documentId: string
  versionId: string
  version: number
  /** The version the reviewer last decided on, when the file changed since. */
  previousVersion: number | null
  sha: string
  pageCount: number | null
  fileName: string
  contentType: string
  sizeBytes: number
}

export function attachmentsOf(detail: SubmissionDetail): ReviewAttachment[] {
  return detail.checklist.flatMap((slot) =>
    slot.attachments.map((a) => ({
      key: `${slot.key}:${a.id}`,
      label: slot.label,
      typeCode: slot.documentType?.code ?? null,
      documentId: a.documentId,
      versionId: a.versionId,
      version: a.versionNumber,
      previousVersion: a.reviewedVersionNumber !== null && a.reviewedVersionNumber !== a.versionNumber ? a.reviewedVersionNumber : null,
      sha: a.sha256,
      pageCount: a.pageCount,
      fileName: a.fileName,
      contentType: a.contentType,
      sizeBytes: a.sizeBytes,
    })),
  )
}

export type DecisionKind = 'approve' | 'revision' | 'reject'

const decisionKinds: Partial<Record<ReviewActionType | string, DecisionKind>> = {
  Approved: 'approve',
  RevisionRequested: 'revision',
  Rejected: 'reject',
}

export interface Decision {
  kind: DecisionKind
  at: string
  stage: number | null
  comment: string | null
}

/** The latest decision the user made on this submission (timeline is oldest first). */
export function lastDecisionBy(timeline: TimelineEntry[], userId: string): Decision | null {
  for (let i = timeline.length - 1; i >= 0; i--) {
    const entry = timeline[i]
    const kind = decisionKinds[entry.kind]
    if (kind && entry.actor?.id === userId) return { kind, at: entry.at, stage: entry.stage, comment: entry.comment }
  }
  return null
}

/** The local calendar day of a date-time, e.g. "2026-10-27". */
export function localDay(value: string) {
  const d = new Date(value)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}

export interface DecidedItem {
  row: SubmissionRow
  decision: Decision
}

export interface ReviewHistory {
  /** Submissions the user decided on today, newest first. */
  decidedToday: DecidedItem[]
  /** Average days from submission to final decision over the user's finished reviews, or null. */
  avgDaysToDecision: number | null
}

/**
 * What the user reviewed: submissions they decided on today (confirmed against each timeline, so a
 * later stage's decision isn't counted as theirs) and their average time to a final decision.
 */
export async function loadReviewHistory(userId: string, signal: AbortSignal): Promise<ReviewHistory> {
  const rows = (await listSubmissions({ scope: 'reviewing', pageSize: 100 }, signal)).items
  const today = todayIso()
  const touchedToday = rows.filter((r) => localDay(r.updatedAt) === today)
  const decided = await Promise.all(
    touchedToday.map(async (row) => {
      const decision = lastDecisionBy(await getTimeline(row.id, signal), userId)
      return decision && localDay(decision.at) === today ? { row, decision } : null
    }),
  )

  const finished = rows.filter(
    (r) => (r.status === 'Approved' || r.status === 'Rejected') && r.reviewer?.id === userId && r.submittedAt && r.finalizedAt,
  )
  const avgDaysToDecision = finished.length
    ? finished.reduce((sum, r) => sum + (Date.parse(r.finalizedAt!) - Date.parse(r.submittedAt!)) / 86_400_000, 0) / finished.length
    : null

  return {
    decidedToday: decided.filter((d): d is DecidedItem => d !== null).sort((a, b) => b.decision.at.localeCompare(a.decision.at)),
    avgDaysToDecision,
  }
}

const honorifics = /^(Dr|Engr|Prof|Atty|Mr|Ms|Mrs|Fr|Sr)\.?$/i

/** "Jamie Santos" → "J. Santos", "Dr. Ana Lim" → "A. Lim". */
export function shortName(name: string) {
  const parts = name.split(/\s+/).filter((p) => p && !honorifics.test(p))
  if (parts.length < 2) return parts[0] ?? name
  return `${parts[0].charAt(0)}. ${parts[parts.length - 1]}`
}

/** "4 h" under a day, then "6 d". */
export const waitingLabel = (hours: number, days: number) => (hours < 24 ? `${Math.max(1, Math.round(hours))} h` : `${days} d`)

/** "BSCS", "CCS" or "CICT": the units the user reviews for. */
export function reviewScope(me: MeDto) {
  const codes = me.roles
    .filter((r) => r.role === 'AcademicSupervisor' || r.role === 'Dean' || r.role === 'DepartmentHead')
    .map((r) => r.scope?.code)
    .filter(Boolean)
  return codes.length ? [...new Set(codes)].join(', ') : 'All'
}

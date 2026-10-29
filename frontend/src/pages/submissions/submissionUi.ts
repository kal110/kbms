import type {
  AcademicTag,
  AttachmentInfo,
  ChecklistItem,
  MeDto,
  RequirementRule,
  RequirementScope,
  ReviewActionType,
  StageInfo,
  SubmissionDetail,
  SubmissionRow,
  SubmissionStatus,
  TermRef,
  UnitRef,
} from '../../api/types'
import type { BadgeTone } from '../../components/Badge'
import { shortDate } from '../../lib/format'

// Display rules for the faculty/staff submission screens (frame 3).

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

const isClosed = (s: SubmissionRow) => s.status === 'Approved' || s.status === 'Rejected'

/** Open work first (by number), then decided ones, newest first. */
export function sortSubmissions(list: SubmissionRow[]) {
  return [...list].sort((a, b) => {
    if (isClosed(a) !== isClosed(b)) return isClosed(a) ? 1 : -1
    if (!isClosed(a)) return a.number.localeCompare(b.number)
    return (b.finalizedAt ?? b.updatedAt).localeCompare(a.finalizedAt ?? a.updatedAt)
  })
}

/** "1st Sem 2026–27" */
export const termShort = (term: TermRef) => `${term.shortName} ${term.yearName.replace(/^AY\s*/, '')}`

/** "1st Sem AY 2026–27" */
export const termLong = (term: TermRef) => `${term.shortName} ${term.yearName}`

export const lowerFirst = (s: string) => s.charAt(0).toLowerCase() + s.slice(1)

/** "Course syllabus" → "Syllabus", "Course outcomes matrix" → "Outcomes matrix" */
export function shortLabel(label: string) {
  const s = label.replace(/^course\s+/i, '')
  return s.charAt(0).toUpperCase() + s.slice(1)
}

const pdfs = (n: number) => (n === 1 ? 'PDF' : 'PDFs')

/** "Exactly 1 PDF", "1–5 PDFs", "Up to 2 PDFs" */
export function filesLabel(rule: RequirementRule, min: number, max: number) {
  if (rule === 'Optional') return `Up to ${max} ${pdfs(max)}`
  if (min === max) return `Exactly ${max} ${pdfs(max)}`
  return `${min}–${max} PDFs`
}

export const ruleOf = (slot: ChecklistItem): RequirementRule => (slot.isRequired ? 'Required' : 'Optional')

/** The submitter's own requirement scope: slots from it carry no "From …" badge. */
export const ownScope = (unit: Pick<UnitRef, 'kind'>): RequirementScope => (unit.kind === 'Program' ? 'Program' : 'Department')

/** Code of a broader scope a slot comes from, for its "From CCS" badge (undefined for the unit's own slots). */
export function sourceCode(source: RequirementScope | null, unit: UnitRef, me: MeDto): string | undefined {
  if (!source || source === ownScope(unit)) return undefined
  if (source === 'College') return unit.collegeCode ?? 'college'
  if (source === 'Department') {
    return me.programs.find((p) => p.department.id === unit.departmentId)?.department.code ?? 'department'
  }
  return unit.code
}

/** Where a file of a type with `tag` is filed: "BSCS / Jamie Santos / AY 2026–27 / 1st Semester". */
/** A per-course submission's term documents go one level further, into the course folder. */
export function filedPath(
  unitCode: string,
  folderName: string,
  term: Pick<TermRef, 'name' | 'yearName'>,
  tag: AcademicTag | 'Term',
  course?: string,
) {
  const parts = [unitCode, folderName]
  if (tag !== 'None') parts.push(term.yearName)
  if (tag === 'Term') parts.push(term.name)
  if (tag === 'Term' && course) parts.push(course)
  return parts.join(' / ')
}

/** The tag every slot's type shares, 'mixed' when they differ, undefined while any is unknown. */
export function sharedTag(tags: (AcademicTag | undefined)[]): AcademicTag | 'mixed' | undefined {
  if (tags.length === 0 || tags.some((t) => t === undefined)) return undefined
  return tags.every((t) => t === tags[0]) ? tags[0] : 'mixed'
}

/** The period tag a slot's files get from the submission's term. */
export function periodTagText(tag: AcademicTag | undefined, term: TermRef) {
  if (!tag || tag === 'None') return 'No period tag'
  return tag === 'Year' ? term.yearName : termLong(term)
}

// ---- Checklist slots

export type SlotTone = 'warn' | 'ok' | 'empty' | 'closed'

function fileState(status: SubmissionStatus, a: AttachmentInfo): { tone: SlotTone; note: string } {
  const pinned = a.pinnedVersionNumber ?? a.versionNumber
  const reviewed = a.reviewedVersionNumber
  switch (status) {
    case 'Draft':
      return { tone: 'ok', note: `v${a.versionNumber} · ${a.fileName} · uploaded ${shortDate(a.uploadedAt)}` }
    case 'RevisionRequested':
      if (reviewed !== null && a.versionNumber > reviewed) {
        return { tone: 'warn', note: `Reviewed v${reviewed} · current draft v${a.versionNumber} (uploaded ${shortDate(a.uploadedAt)})` }
      }
      if (reviewed === null) return { tone: 'ok', note: `v${a.versionNumber} · added after review` }
      return { tone: 'ok', note: `v${a.versionNumber} · unchanged since review` }
    case 'Resubmitted':
      if (reviewed !== null && pinned > reviewed) return { tone: 'ok', note: `v${pinned} resubmitted · reviewer last saw v${reviewed}` }
      return { tone: 'ok', note: `v${pinned} · unchanged since review` }
    case 'Approved':
      return { tone: 'ok', note: `v${pinned} · approved and published` }
    case 'Rejected':
      return { tone: 'closed', note: `v${pinned} · not published` }
    default:
      return { tone: 'ok', note: `v${pinned} · pinned for review` }
  }
}

export function slotState(status: SubmissionStatus, slot: ChecklistItem): { tone: SlotTone; note: string } {
  const files = slot.attachments
  if (files.length === 0) {
    if (status === 'RevisionRequested' && slot.isRequired) return { tone: 'warn', note: 'Not attached · required before you resubmit' }
    return { tone: 'empty', note: 'Not attached' }
  }
  if (files.length === 1) return fileState(status, files[0])
  // Several files: name each, and flag the slot when any of them needs attention.
  const states = files.map((a) => fileState(status, a))
  const tone = states.find((s) => s.tone === 'warn')?.tone ?? states[0].tone
  return { tone, note: `${files.length} files · ${files.map((a) => `${a.fileName} v${a.versionNumber}`).join(', ')}` }
}

/** Files whose current version is newer than what was last submitted (or that were added since). */
export const changedSinceSubmit = (s: SubmissionDetail) =>
  s.checklist.flatMap((slot) =>
    slot.attachments
      .filter((a) => a.pinnedVersionNumber === null || a.versionNumber > a.pinnedVersionNumber)
      .map((a) => ({ label: slot.label, version: a.versionNumber })),
  )

/** "Syllabus v2 + Outcomes matrix v1" */
export const versionList = (items: { label: string; version: number }[], separator = ' + ') =>
  items.map((i) => `${shortLabel(i.label)} v${i.version}`).join(separator)

export const missingRequired = (s: SubmissionDetail) => s.checklist.filter((slot) => slot.isRequired && !slot.satisfied)

// ---- Reviewers and comments

/** The stage the submission waits at, or stage 1 (with its predicted reviewer) for a draft. */
export const currentStage = (s: SubmissionDetail): StageInfo | undefined =>
  s.stages.find((st) => st.state === 'Current') ?? s.stages.find((st) => st.order === s.currentStage) ?? s.stages[0]

/** "Dr. Ana Lim · Academic Supervisor, BSCS" */
export function reviewerLabel(s: SubmissionDetail) {
  const stage = currentStage(s)
  const reviewer = s.assignment?.reviewer ?? stage?.reviewer
  if (!reviewer) return 'not assigned yet'
  const unit = stage?.rule === 'CollegeDean' ? s.unit.collegeCode : s.unit.code
  const role = [reviewer.title, unit].filter(Boolean).join(', ')
  return role ? `${reviewer.name} · ${role}` : reviewer.name
}

const commentContext: Partial<Record<ReviewActionType, string>> = {
  RevisionRequested: 'revision request',
  Rejected: 'rejection explanation',
  Approved: 'approval note',
  Commented: 'comment',
  Reassigned: 'reassignment',
}

export const commentLabel = (action: ReviewActionType, stage: number) =>
  [commentContext[action] ?? lowerFirst(action), stage > 1 ? `stage ${stage}` : ''].filter(Boolean).join(' · ')

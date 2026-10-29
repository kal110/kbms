import { useMemo, useState } from 'react'
import { getMyCourses } from '../../api/org'
import { getAcademicYears, getActiveTerm, getDocumentTypes, getSubmissionTypes, termLabel } from '../../api/reference'
import { getEffectiveChecklist } from '../../api/requirements'
import type { EffectiveChecklistDto, MeDto, MyCourseDto, SubmissionTypeDto } from '../../api/types'
import { createSubmission } from '../../api/workflow'
import { useAction, useApi } from '../../api/useApi'
import { useMe } from '../../auth/SessionContext'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { TypeChip } from '../../components/Chips'
import { Modal } from '../../components/Modal'
import { filedPath, filesLabel, lowerFirst, sharedTag, statusMeta } from './submissionUi'
import styles from './MySubmissionsPage.module.css'

interface NewSubmissionModalProps {
  /** The submitter's own folder name (from /folders/me), when known. */
  folderName?: string
  onClose: () => void
  onCreated: (id: string) => void
}

/**
 * Where a submission is filed and reviewed: the caller's program (the primary one, or a course's) or a department they
 * work in or head. Someone who is both faculty and staff (or heads CICT) has several.
 */
interface SubmitUnit {
  key: string
  code: string
  /** Set for a department; a program submission names no unit and gets the primary program (or the course's). */
  departmentId?: string
}

/** A type the caller can submit for one unit, with its checklist: one for per-person types, one per course program for per-course types. */
interface TypeOption {
  key: string
  type: SubmissionTypeDto
  unit: SubmitUnit
  checklist?: EffectiveChecklistDto
  byProgram: Record<string, EffectiveChecklistDto>
}

const courseName = (c: MyCourseDto['course']) => `${c.code} · ${c.title}`

const typeOption = (o: TypeOption) => (
  <option key={o.key} value={o.key}>
    {o.type.name}
    {o.type.perCourse ? ' (one per course)' : ''}
  </option>
)

function unitsOf(me: MeDto): SubmitUnit[] {
  const supervised = me.roles.find((r) => r.role === 'AcademicSupervisor')?.scope
  const programCode = me.programs.find((p) => p.isPrimary)?.program.code ?? me.programs[0]?.program.code ?? supervised?.code
  const departments = [
    ...me.departments.map((d) => d.department),
    ...me.roles.flatMap((r) => (r.role === 'DepartmentHead' && r.scope ? [r.scope] : [])),
  ]
  return [
    ...(programCode ? [{ key: 'program', code: programCode }] : []),
    ...[...new Map(departments.map((d) => [d.id, d])).values()].map((d) => ({ key: d.id, code: d.code, departmentId: d.id })),
  ]
}

export function NewSubmissionModal({ folderName, onClose, onCreated }: NewSubmissionModalProps) {
  const me = useMe()
  const reference = useApi(
    () => Promise.all([getSubmissionTypes(), getAcademicYears(), getActiveTerm(), getDocumentTypes()]),
    [],
  )
  const [submissionTypes, years, activeTerm, documentTypes] = reference.data ?? []

  const units = useMemo(() => unitsOf(me), [me])
  const [choice, setChoice] = useState('')
  const [courseId, setCourseId] = useState('')
  const [title, setTitle] = useState('')
  const [titleEdited, setTitleEdited] = useState(false)
  const [pickedTermId, setPickedTermId] = useState<string | null>(null)
  const [tried, setTried] = useState(false)
  const termId = pickedTermId ?? activeTerm?.id ?? ''

  // The courses the caller teaches this term (from the teaching load).
  const courses = useApi((signal) => (termId ? getMyCourses({ termId }, signal) : Promise.resolve([])), [termId])

  // Each type's checklist for this person, term and unit: only types with requirements can be submitted. A per-course
  // type's checklist comes from the program of the course, so it is looked up once per program taught.
  const checklists = useApi(async (): Promise<TypeOption[]> => {
    if (!submissionTypes || !termId || !courses.data) return []
    const programIds = [...new Set(courses.data.map((c) => c.course.program.id))]
    const program = units.find((u) => !u.departmentId)
    const perType = await Promise.all(
      submissionTypes.map(async (type): Promise<TypeOption[]> => {
        if (!type.perCourse)
          return Promise.all(
            units.map(async (unit) => ({
              key: `${type.id}:${unit.key}`,
              type,
              unit,
              checklist: await getEffectiveChecklist({ submissionTypeId: type.id, termId, departmentId: unit.departmentId }),
              byProgram: {},
            })),
          )
        // Courses belong to programs: staff with no program have no per-course types.
        if (!program) return []
        const lists = await Promise.all(
          programIds.map((programId) =>
            getEffectiveChecklist({ submissionTypeId: type.id, termId, programId }).then((c) => [programId, c] as const),
          ),
        )
        return [{ key: `${type.id}:${program.key}`, type, unit: program, byProgram: Object.fromEntries(lists.filter(([, c]) => c.items.length > 0)) }]
      }),
    )
    return perType.flat()
  }, [submissionTypes, termId, courses.data, units])

  const options = useMemo(
    () =>
      (checklists.data ?? [])
        .filter((o) => (o.type.perCourse ? Object.keys(o.byProgram).length > 0 : (o.checklist?.items.length ?? 0) > 0))
        .sort((a, b) => a.type.name.localeCompare(b.type.name)),
    [checklists.data],
  )
  // Faculty who are also staff (or head an office) pick the type under the unit it is for.
  const multiUnit = new Set(options.map((o) => o.unit.key)).size > 1
  // Per-course types the caller has no course for this term: said out loud rather than silently missing.
  const noCourseTypes = (checklists.data ?? []).filter((o) => o.type.perCourse && Object.keys(o.byProgram).length === 0)
  const chosen = options.find((o) => o.key === choice)
  const typeId = chosen?.type.id ?? ''
  const perCourse = chosen?.type.perCourse ?? false

  // For a per-course type: the caller's courses with their live submission of that type, if any.
  const typeCourses = useApi(
    (signal) => (perCourse && termId ? getMyCourses({ termId, submissionTypeId: typeId }, signal) : Promise.resolve(null)),
    [perCourse, typeId, termId],
  )
  const course = typeCourses.data?.find((c) => c.course.id === courseId)?.course

  const checklist = perCourse ? (course ? chosen?.byProgram[course.program.id] : undefined) : chosen?.checklist
  const items = checklist?.items ?? []
  const tagOf = (documentTypeId: string) => documentTypes?.find((t) => t.id === documentTypeId)?.tag
  const needsPeriod = perCourse || items.some((item) => tagOf(item.documentTypeId) !== 'None')
  // Once a type is chosen and all its slots share a tag, the exact folder is known.
  const shared = sharedTag(items.map((item) => tagOf(item.documentTypeId)))
  const exactTag = shared === 'mixed' ? undefined : shared
  const unitScope = chosen?.unit.departmentId ? 'Department' : 'Program'
  const unitCode = course?.program.code ?? chosen?.unit.code ?? units[0]?.code
  const ownSet = items.some((item) => item.source?.type === unitScope)

  const openTerms = (years ?? []).flatMap((y) => y.terms).filter((t) => t.status !== 'Closed').sort((a, b) => a.startsOn.localeCompare(b.startsOn))
  const term = openTerms.find((t) => t.id === termId)

  const chooseType = (key: string) => {
    setChoice(key)
    setCourseId('')
  }

  const chooseCourse = (id: string) => {
    setCourseId(id)
    const picked = typeCourses.data?.find((c) => c.course.id === id)?.course
    // Suggest "Course syllabus — CS 301 Data Structures" until the user writes their own title.
    if (picked && chosen && (!titleEdited || !title.trim())) setTitle(`${chosen.type.name} — ${picked.code} ${picked.title}`)
  }

  const create = useAction(() =>
    createSubmission({
      submissionTypeId: typeId,
      title: title.trim(),
      // A checklist without period tags is filed under the active term.
      academicTermId: needsPeriod ? termId : null,
      programId: null,
      courseId: perCourse ? courseId : null,
      departmentId: perCourse ? null : (chosen?.unit.departmentId ?? null),
    }),
  )

  const submit = async () => {
    setTried(true)
    if (!typeId || !title.trim() || (perCourse && !courseId)) return
    const created = await create.run()
    if (created) onCreated(created.id)
  }

  return (
    <Modal
      title="New submission"
      description="Pick what you are submitting. The checklist and each file's type come from the requirement set."
      width={560}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" disabled={create.pending || !reference.data} onClick={submit}>
            {create.pending ? 'Creating…' : 'Create draft'}
          </Button>
        </>
      }
    >
      {reference.error ? (
        <ErrorState error={reference.error} onRetry={reference.reload} compact />
      ) : !reference.data ? (
        <Loading compact />
      ) : (
        <>
          <label className={styles.formField}>
            <span className={styles.formLabel}>
              Submission type <span className={styles.requiredMark}>*</span>
            </span>
            <select className="field" value={choice} onChange={(e) => chooseType(e.target.value)} disabled={checklists.loading}>
              <option value="">{checklists.loading ? 'Loading submission types…' : 'Choose a submission type…'}</option>
              {multiUnit
                ? units.map((unit) => {
                    const own = options.filter((o) => o.unit.key === unit.key)
                    return (
                      own.length > 0 && (
                        <optgroup key={unit.key} label={`For ${unit.code}`}>
                          {own.map(typeOption)}
                        </optgroup>
                      )
                    )
                  })
                : options.map(typeOption)}
            </select>
            {multiUnit && (
              <span className={styles.formHelp}>
                Grouped by unit: a submission is filed and reviewed in the unit you pick it under.
              </span>
            )}
            {checklists.error || courses.error ? <InlineError error={checklists.error ?? courses.error} /> : null}
            {!checklists.loading && !checklists.error && options.length === 0 && (
              <span className={styles.formHelp}>Nothing is required from you for this term yet.</span>
            )}
            {!checklists.loading && noCourseTypes.length > 0 && courses.data?.length === 0 && (
              <span className={styles.formHelp}>
                {noCourseTypes.map((o) => o.type.name).join(', ')} {noCourseTypes.length === 1 ? 'is' : 'are'} submitted per course, and
                you have no courses in this term’s teaching load.
              </span>
            )}
            {tried && !typeId && <span className={styles.formError}>Choose a submission type.</span>}
          </label>

          {perCourse && chosen && (
            <label className={styles.formField}>
              <span className={styles.formLabel}>
                Course <span className={styles.requiredMark}>*</span>
              </span>
              <select className="field" value={courseId} onChange={(e) => chooseCourse(e.target.value)} disabled={!typeCourses.data}>
                <option value="">{typeCourses.data ? 'Choose the course this is for…' : 'Loading your courses…'}</option>
                {(typeCourses.data ?? []).map((c) => {
                  const listed = Boolean(chosen.byProgram[c.course.program.id])
                  const sections = c.sections.length ? ` (${c.sections.join(', ')})` : ''
                  const note = c.submission
                    ? ` — ${c.submission.number}, ${lowerFirst(statusMeta[c.submission.status].label)}`
                    : !listed
                      ? ` — ${c.course.program.code} has no ${lowerFirst(chosen.type.name)} list`
                      : ''
                  return (
                    <option key={c.course.id} value={c.course.id} disabled={Boolean(c.submission) || !listed}>
                      {c.course.program.code} · {courseName(c.course)}
                      {sections}
                      {note}
                    </option>
                  )
                })}
              </select>
              <InlineError error={typeCourses.error} />
              <span className={styles.formHelp}>
                {chosen.type.name} is submitted once per course you teach. Courses come from the {term ? termLabel(term) : 'term'} teaching
                load; one that already has a submission continues there.
              </span>
              {tried && !courseId && <span className={styles.formError}>Choose the course.</span>}
            </label>
          )}

          <label className={styles.formField}>
            <span className={styles.formLabel}>
              Title <span className={styles.requiredMark}>*</span>
            </span>
            <input
              className="field"
              value={title}
              placeholder="e.g. Course Syllabus — CS 301 Data Structures"
              onChange={(e) => {
                setTitle(e.target.value)
                setTitleEdited(true)
              }}
            />
            {tried && !title.trim() && <span className={styles.formError}>Give the submission a title.</span>}
          </label>

          {(!typeId || needsPeriod) && (
            <label className={styles.formField}>
              <span className={styles.formLabel}>Academic term</span>
              <select
                className="field"
                value={termId}
                onChange={(e) => {
                  setPickedTermId(e.target.value)
                  chooseType('')
                }}
              >
                {openTerms.map((t) => (
                  <option key={t.id} value={t.id}>
                    {termLabel(t)}
                    {t.status === 'Active' ? ' (current)' : ''}
                  </option>
                ))}
              </select>
              <span className={styles.formHelp}>
                Every file you attach is tagged with this term (or its academic year)
                {folderName && unitCode && term
                  ? ` and filed ${exactTag ? 'in' : 'under'} ${filedPath(
                      unitCode,
                      folderName,
                      { name: term.name, yearName: term.academicYearName },
                      exactTag ?? 'None',
                      course && courseName(course),
                    )}.`
                  : '.'}
              </span>
            </label>
          )}

          {chosen && (perCourse ? course : true) && (
            <div className={styles.formField}>
              <span className={styles.formLabel}>Checklist{ownSet && unitCode ? ` · ${unitCode} requirement set` : ''}</span>
              <div className={styles.previewList}>
                {items.map((item) => (
                  <div key={item.key} className={styles.previewItem}>
                    <span className={styles.slotText}>
                      <span className={styles.slotTitle}>
                        {item.label} · {lowerFirst(item.rule)}
                      </span>
                      <span className={styles.slotNote}>{filesLabel(item.rule, item.files.min, item.files.max)}</span>
                    </span>
                    {item.source && item.source.type !== unitScope && <Badge tone="purple">From {item.source.code}</Badge>}
                    <TypeChip label={item.documentTypeCode} />
                  </div>
                ))}
              </div>
            </div>
          )}

          <InlineError error={create.error} />
        </>
      )}
    </Modal>
  )
}

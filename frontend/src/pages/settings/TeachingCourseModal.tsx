import { useEffect, useMemo, useState } from 'react'
import { ApiError } from '../../api/client'
import { getOrgTree, getProgramCourses, getUserForAdmin, removeTeachingCourse, searchPeople, setTeachingCourse } from '../../api/org'
import type { CourseRefDto, PersonDto, TeachingAssignmentDto, TeachingLoadChangeDto } from '../../api/types'
import { useAction, useApi } from '../../api/useApi'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Avatar } from '../../components/Avatar'
import { Button } from '../../components/Button'
import { Modal } from '../../components/Modal'
import panel from './panel.module.css'
import styles from './TeachingLoadsPage.module.css'

// Encoding a teaching load by hand (Settings → Teaching loads): the same lines the registrar's file
// holds, one faculty member's course at a time. Adding keeps the person and program for the next
// course; editing sets a course's sections, swaps a course encoded wrongly, or removes it.

const NEW_COURSE = 'new'

/** One chip of the load: a person's course in the term, with its sections. */
export interface CourseEntry {
  person: PersonDto
  programId: string
  course: { id: string; code: string; title: string }
  sections: string[]
}

interface TeachingCourseModalProps {
  termId: string
  termName: string
  /** The term's load, to say which courses the person already teaches. */
  assignments: TeachingAssignmentDto[]
  /** Editing this course; without it the modal adds courses, one after another. */
  entry?: CourseEntry
  /** Starts adding for this person (and program), e.g. from the + on their row. */
  preset?: { person: PersonDto; programId: string }
  onChanged: (change: TeachingLoadChangeDto, message: string) => void
  onClose: () => void
}

/** "a, b; C" → ["A", "B", "C"]: sections are stored in capitals, each once. */
const parseSections = (text: string) => [...new Set(text.split(/[,;]+/).map((s) => s.trim().toUpperCase()).filter(Boolean))]

const withSections = (code: string, sections: string[]) => (sections.length ? `${code} (${sections.join(', ')})` : code)

const sameSet = (a: string[], b: string[]) => a.length === b.length && a.every((x) => b.includes(x))

const fieldError = (error: unknown, field: string) =>
  error instanceof ApiError ? error.errors?.[field]?.join(' ') : undefined

const formError = (error: unknown) =>
  error instanceof ApiError && error.errors && Object.keys(error.errors).length ? undefined : error

export function TeachingCourseModal({ termId, termName, assignments, entry, preset, onChanged, onClose }: TeachingCourseModalProps) {
  const editing = Boolean(entry)
  const [person, setPerson] = useState<PersonDto | null>(entry?.person ?? preset?.person ?? null)
  const [programId, setProgramId] = useState(entry?.programId ?? preset?.programId ?? '')
  const [courseId, setCourseId] = useState(entry?.course.id ?? '')
  const [newCode, setNewCode] = useState('')
  const [newTitle, setNewTitle] = useState('')
  const [sectionsText, setSectionsText] = useState(entry?.sections.join(', ') ?? '')
  const [added, setAdded] = useState<string[]>([])
  const [confirmRemove, setConfirmRemove] = useState(false)

  const tree = useApi(() => getOrgTree(), [])
  const programs = useMemo(
    () =>
      (tree.data?.colleges ?? [])
        .flatMap((c) => c.departments.flatMap((d) => d.programs))
        .sort((a, b) => a.code.localeCompare(b.code)),
    [tree.data],
  )
  const program = programs.find((p) => p.id === programId)
  const catalog = useApi(
    (signal) => (programId ? getProgramCourses(programId, signal) : Promise.resolve<CourseRefDto[]>([])),
    [programId],
  )

  // The person's courses this term: course id → sections.
  const teaching = useMemo(() => {
    const map = new Map<string, string[]>()
    for (const a of assignments) {
      if (a.person.id !== person?.id) continue
      map.set(a.course.id, [...(map.get(a.course.id) ?? []), ...(a.section ? [a.section] : [])])
    }
    return map
  }, [assignments, person])

  const isNew = courseId === NEW_COURSE
  const typed = parseSections(sectionsText)
  const already = courseId && !isNew && courseId !== entry?.course.id ? teaching.get(courseId) : undefined
  // Adding (or switching to) a course the person already teaches adds sections to it, never drops the ones they have.
  const sections = already ? [...new Set([...already, ...typed])].sort() : typed
  const unchanged = editing && entry && courseId === entry.course.id && sameSet(sections, entry.sections)
  const ready = Boolean(person && programId && courseId && (!isNew || newCode.trim()) && !unchanged)

  const save = useAction(() =>
    setTeachingCourse({
      academicTermId: termId,
      userId: person!.id,
      programId,
      courseId: isNew ? null : courseId,
      courseCode: isNew ? newCode.trim() : null,
      courseTitle: isNew ? newTitle.trim() || null : null,
      sections,
      replacesCourseId: entry && courseId !== entry.course.id ? entry.course.id : null,
    }),
  )
  const remove = useAction(() => removeTeachingCourse(termId, entry!.person.id, entry!.course.id))

  // A person picked while no program is chosen: start from their primary program.
  const pick = async (next: PersonDto) => {
    setPerson(next)
    setConfirmRemove(false)
    if (programId) return
    const user = await getUserForAdmin(next.id).catch(() => null)
    const home = user?.programs.find((m) => m.isPrimary) ?? user?.programs[0]
    if (home) setProgramId((current) => current || home.program.id)
  }

  const submit = async () => {
    if (!ready || !person) return
    const change = await save.run()
    if (!change) return
    const code = change.lines[0]?.course.code ?? newCode.trim().toUpperCase()
    const lineSections = change.lines.map((l) => l.section).filter((s): s is string => Boolean(s))
    const extras = [
      change.courseCreated ? `${code} joined the ${program?.code} course catalog.` : '',
      change.membershipAdded ? `${person.displayName} joined the ${program?.code} faculty.` : '',
    ].filter(Boolean)
    const message = [
      editing ? `Saved ${withSections(code, lineSections)} for ${person.displayName}.` : `Added ${withSections(code, lineSections)} to ${person.displayName}’s load.`,
      ...extras,
    ].join(' ')
    onChanged(change, message)
    if (editing) return onClose()

    setAdded((list) => [...list, withSections(code, lineSections)])
    setCourseId('')
    setNewCode('')
    setNewTitle('')
    setSectionsText('')
    if (change.courseCreated) catalog.reload()
  }

  const removeCourse = async () => {
    if (!entry) return
    const change = await remove.run()
    if (!change) return
    onChanged(change, `Removed ${withSections(entry.course.code, entry.sections)} from ${entry.person.displayName}’s load.`)
    onClose()
  }

  const footer = confirmRemove && entry ? (
    <>
      <span className={styles.confirmText}>
        Remove {withSections(entry.course.code, entry.sections)} from {entry.person.displayName}’s {termName} load?
      </span>
      <Button onClick={() => setConfirmRemove(false)} disabled={remove.pending}>
        Keep it
      </Button>
      <Button variant="danger" onClick={() => void removeCourse()} disabled={remove.pending}>
        {remove.pending ? 'Removing…' : 'Remove'}
      </Button>
    </>
  ) : (
    <>
      {editing && (
        <Button variant="ghost" className={styles.removeButton} onClick={() => setConfirmRemove(true)}>
          Remove from load
        </Button>
      )}
      <span className={panel.spacer} />
      <Button onClick={onClose}>{!editing && added.length ? 'Done' : 'Cancel'}</Button>
      <Button variant="primary" disabled={!ready || save.pending} onClick={() => void submit()}>
        {save.pending ? 'Saving…' : editing ? 'Save' : already ? 'Add sections' : 'Add to load'}
      </Button>
    </>
  )

  return (
    <Modal
      title={entry ? `${entry.course.code} · ${entry.course.title}` : 'Encode teaching load'}
      description={
        entry
          ? `${entry.person.displayName}’s ${termName} load.`
          : `Add courses to a faculty member’s ${termName} load, one at a time.`
      }
      width={620}
      onClose={onClose}
      footer={footer}
    >
      {editing ? null : person ? (
        <div className={panel.field}>
          <span className={panel.label}>Faculty member</span>
          <div className={styles.picked}>
            <Avatar initials={person.initials} size={28} />
            <span className={styles.personText}>
              <span className={styles.personName}>{person.displayName}</span>
              <span className={styles.personMeta}>{[person.externalId, person.email].filter(Boolean).join(' · ')}</span>
            </span>
            <Button size="sm" variant="ghost" onClick={() => setPerson(null)}>
              Change
            </Button>
          </div>
        </div>
      ) : (
        <PersonPicker onPick={(p) => void pick(p)} />
      )}

      <div className={panel.fieldRow}>
        <label className={panel.field} style={{ width: 190, flexShrink: 0 }}>
          <span className={panel.label}>Program</span>
          <select
            className={`${panel.select} ${fieldError(save.error, 'programId') ? panel.inputInvalid : ''}`}
            value={programId}
            onChange={(e) => {
              setProgramId(e.target.value)
              setCourseId('')
            }}
          >
            <option value="">{tree.loading ? 'Loading…' : 'Pick a program'}</option>
            {programs.map((p) => (
              <option key={p.id} value={p.id}>
                {p.code} · {p.name}
              </option>
            ))}
          </select>
        </label>
        <label className={`${panel.field} ${panel.fill}`}>
          <span className={panel.label}>Course</span>
          <select
            className={`${panel.select} ${fieldError(save.error, 'courseId') ? panel.inputInvalid : ''}`}
            value={courseId}
            disabled={!programId}
            onChange={(e) => setCourseId(e.target.value)}
          >
            <option value="">{catalog.loading ? 'Loading courses…' : programId ? 'Pick a course' : 'Pick a program first'}</option>
            {(catalog.data ?? []).map((c) => {
              const taught = !editing || c.id !== entry?.course.id ? teaching.get(c.id) : undefined
              return (
                <option key={c.id} value={c.id}>
                  {c.code} · {c.title}
                  {taught ? ` — teaching${taught.length ? ` ${taught.join(', ')}` : ''}` : ''}
                </option>
              )
            })}
            {programId && <option value={NEW_COURSE}>New course…</option>}
          </select>
          {catalog.error ? <InlineError error={catalog.error} /> : null}
          {fieldError(save.error, 'courseId') && <span className={panel.error}>{fieldError(save.error, 'courseId')}</span>}
        </label>
      </div>

      {isNew && (
        <div className={panel.fieldRow}>
          <label className={panel.field} style={{ width: 150, flexShrink: 0 }}>
            <span className={panel.label}>Course code</span>
            <input
              className={`${panel.input} ${fieldError(save.error, 'courseCode') ? panel.inputInvalid : ''}`}
              value={newCode}
              maxLength={40}
              autoFocus
              placeholder="e.g. CS 350"
              onChange={(e) => setNewCode(e.target.value)}
            />
          </label>
          <label className={`${panel.field} ${panel.fill}`}>
            <span className={panel.label}>Course title</span>
            <input
              className={`${panel.input} ${fieldError(save.error, 'courseTitle') ? panel.inputInvalid : ''}`}
              value={newTitle}
              maxLength={200}
              placeholder="e.g. Machine Learning"
              onChange={(e) => setNewTitle(e.target.value)}
            />
          </label>
        </div>
      )}
      {isNew && (fieldError(save.error, 'courseCode') || fieldError(save.error, 'courseTitle')) ? (
        <span className={panel.error}>{fieldError(save.error, 'courseCode') ?? fieldError(save.error, 'courseTitle')}</span>
      ) : isNew ? (
        <span className={panel.help}>
          Joins the {program?.code ?? 'program’s'} course catalog. A code it already has is reused, whatever the title.
        </span>
      ) : null}

      <label className={panel.field}>
        <span className={panel.label}>Sections</span>
        <input
          className={`${panel.input} ${fieldError(save.error, 'sections') ? panel.inputInvalid : ''}`}
          value={sectionsText}
          placeholder="e.g. A, B"
          onChange={(e) => setSectionsText(e.target.value)}
        />
        {fieldError(save.error, 'sections') ? (
          <span className={panel.error}>{fieldError(save.error, 'sections')}</span>
        ) : already ? (
          <span className={panel.help}>
            {person?.displayName} already teaches this course{already.length ? ` (${already.join(', ')})` : ''}; new sections are
            added to it.
          </span>
        ) : (
          <span className={panel.help}>Separate with commas. Leave empty when the course isn’t split into sections.</span>
        )}
      </label>

      {added.length > 0 && (
        <div className={styles.added} role="status">
          <span className={styles.addedTitle}>Added for {person?.displayName ?? 'them'} so far</span>
          <span>{added.join(' · ')}</span>
        </div>
      )}
      <InlineError error={formError(save.error) ?? remove.error} />
    </Modal>
  )
}

function PersonPicker({ onPick }: { onPick: (person: PersonDto) => void }) {
  const [query, setQuery] = useState('')
  const [debounced, setDebounced] = useState('')

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(query.trim()), 250)
    return () => clearTimeout(timer)
  }, [query])

  const people = useApi((signal) => searchPeople(debounced, 1, 6, signal), [debounced])

  return (
    <div className={panel.field}>
      <span className={panel.label}>Faculty member</span>
      <input
        className={panel.input}
        type="search"
        autoFocus
        placeholder="Find a person by name or email"
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        aria-label="Find a faculty member"
      />
      <div className={styles.people}>
        {people.error ? (
          <ErrorState compact error={people.error} onRetry={people.reload} />
        ) : !people.data ? (
          <Loading compact />
        ) : people.data.items.length === 0 ? (
          <span className={styles.peopleEmpty}>
            Nobody matches. Add new people in Users & roles, or let the registrar’s file register them.
          </span>
        ) : (
          people.data.items.map((p) => (
            <button key={p.id} type="button" className={styles.personOption} onClick={() => onPick(p)}>
              <Avatar initials={p.initials} size={24} />
              <span className={styles.personText}>
                <span className={styles.personName}>{p.displayName}</span>
                <span className={styles.personMeta}>{[p.externalId, p.email].filter(Boolean).join(' · ')}</span>
              </span>
            </button>
          ))
        )}
      </div>
    </div>
  )
}

import { useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { AlertTriangle, CircleAlert, Download, FileSpreadsheet, PencilLine, Plus, Search, Upload } from 'lucide-react'
import { getTeachingLoad, importTeachingLoad } from '../../api/org'
import { getAcademicYears, termLabel } from '../../api/reference'
import type {
  AcademicTermDto,
  ImportIssueDto,
  ImportLoadLineDto,
  TeachingAssignmentDto,
  TeachingImportResultDto,
  TeachingLoadChangeDto,
} from '../../api/types'
import { useAction, useApi } from '../../api/useApi'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Avatar } from '../../components/Avatar'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { FilterChip } from '../../components/FilterChip'
import { Modal } from '../../components/Modal'
import { SettingsFrame } from './SettingsFrame'
import { TeachingCourseModal } from './TeachingCourseModal'
import type { CourseEntry } from './TeachingCourseModal'
import panel from './panel.module.css'
import styles from './TeachingLoadsPage.module.css'

// Settings → Teaching loads: who teaches which course of which program, per term. The registrar's
// file (CSV or Excel) replaces a term's load, and courses can be encoded by hand too; the load
// decides each program's faculty for the dashboard and the courses faculty pick when a submission
// type is one per course.

const MAX_FILE_BYTES = 5 * 1024 * 1024

const TEMPLATE = [
  'faculty_id,faculty_name,email,program,course_code,course_title,section',
  'u-jamie,Jamie Santos,jamie.santos@usa.edu.ph,BSCS,CS 301,Data Structures,A',
  'u-jamie,Jamie Santos,jamie.santos@usa.edu.ph,BSIT,IT 214,Systems Integration and Architecture,A',
].join('\r\n')

const columns: { name: string; note: string; required?: boolean }[] = [
  { name: 'faculty_id', note: 'Sign-in id or employee no. (or email)', required: true },
  { name: 'faculty_name', note: 'Needed for people not registered yet' },
  { name: 'email', note: 'Matches people when there’s no id' },
  { name: 'program', note: 'Program code, e.g. BSCS', required: true },
  { name: 'course_code', note: 'e.g. CS 301', required: true },
  { name: 'course_title', note: 'Needed for new courses; renames others' },
  { name: 'section', note: 'Optional, e.g. A' },
]

interface CourseLine {
  id: string
  code: string
  title: string
  sections: string[]
}

interface PersonLine {
  person: TeachingAssignmentDto['person']
  courses: CourseLine[]
}

interface ProgramGroup {
  program: TeachingAssignmentDto['course']['program']
  people: PersonLine[]
  courseCount: number
}

/** Program → person → courses (with their sections), each sorted. */
function group(assignments: TeachingAssignmentDto[]): ProgramGroup[] {
  const programs = new Map<string, { program: ProgramGroup['program']; people: Map<string, PersonLine>; courses: Set<string> }>()
  for (const a of assignments) {
    const p = programs.get(a.course.program.id) ?? { program: a.course.program, people: new Map<string, PersonLine>(), courses: new Set<string>() }
    programs.set(a.course.program.id, p)
    p.courses.add(a.course.id)
    const person = p.people.get(a.person.id) ?? { person: a.person, courses: [] }
    p.people.set(a.person.id, person)
    const course = person.courses.find((c) => c.id === a.course.id)
    if (course) {
      if (a.section) course.sections.push(a.section)
    } else person.courses.push({ id: a.course.id, code: a.course.code, title: a.course.title, sections: a.section ? [a.section] : [] })
  }
  return [...programs.values()]
    .map((p) => ({
      program: p.program,
      courseCount: p.courses.size,
      people: [...p.people.values()]
        .map((x) => ({ ...x, courses: x.courses.sort((a, b) => a.code.localeCompare(b.code)) }))
        .sort((a, b) => a.person.displayName.localeCompare(b.person.displayName)),
    }))
    .sort((a, b) => a.program.code.localeCompare(b.program.code))
}

const plural = (n: number, one: string, many = `${one}s`) => `${n.toLocaleString('en-US')} ${n === 1 ? one : many}`

const formatDate = (iso: string) =>
  new Date(iso).toLocaleString('en-US', { month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit' })

function downloadTemplate() {
  const url = URL.createObjectURL(new Blob([TEMPLATE], { type: 'text/csv;charset=utf-8' }))
  const link = document.createElement('a')
  link.href = url
  link.download = 'teaching-load-template.csv'
  link.click()
  URL.revokeObjectURL(url)
}

const readBase64 = (file: File) =>
  new Promise<string>((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ''))
    reader.onerror = () => reject(reader.error ?? new Error('The file couldn’t be read.'))
    reader.readAsDataURL(file)
  })

// ---- page

export function TeachingLoadsPage() {
  const years = useApi(() => getAcademicYears(), [])
  const terms = useMemo(
    () => (years.data ?? []).flatMap((y) => y.terms).sort((a, b) => b.startsOn.localeCompare(a.startsOn)),
    [years.data],
  )
  const [pickedTermId, setPickedTermId] = useState<string | null>(null)
  const termId = pickedTermId ?? terms.find((t) => t.status === 'Active')?.id ?? terms[0]?.id ?? ''
  const term = terms.find((t) => t.id === termId)

  const load = useApi((signal) => (termId ? getTeachingLoad(termId, signal) : Promise.resolve(undefined)), [termId])
  const [query, setQuery] = useState('')
  const [programId, setProgramId] = useState('all')
  const [importing, setImporting] = useState(false)
  const [applied, setApplied] = useState<TeachingImportResultDto | null>(null)
  const [encoding, setEncoding] = useState<{ entry?: CourseEntry; preset?: { person: CourseEntry['person']; programId: string } } | null>(null)
  const [notice, setNotice] = useState<{ termId: string; text: string; note?: string | null } | null>(null)
  // A closed term's load stays as it was.
  const canEdit = Boolean(term && term.status !== 'Closed')

  const groups = useMemo(() => group(load.data?.assignments ?? []), [load.data])
  const q = query.trim().toLowerCase()
  const visible = groups
    .filter((g) => programId === 'all' || g.program.id === programId)
    .map((g) => ({
      ...g,
      people: g.people.filter(
        (p) =>
          !q ||
          [p.person.displayName, p.person.externalId, p.person.email ?? '', ...p.courses.flatMap((c) => [c.code, c.title])].some((v) =>
            v.toLowerCase().includes(q),
          ),
      ),
    }))
    .filter((g) => g.people.length > 0)
  const last = load.data?.lastImport

  const done = (result: TeachingImportResultDto) => {
    setImporting(false)
    setApplied(result)
    setNotice(null)
    setPickedTermId(result.term.id)
    load.reload()
  }

  const changed = (change: TeachingLoadChangeDto, text: string) => {
    setApplied(null)
    setNotice({ termId, text, note: change.note })
    load.reload()
  }

  return (
    <SettingsFrame
      actions={
        <>
          <Button icon={Download} onClick={downloadTemplate}>
            Template
          </Button>
          <Button
            icon={PencilLine}
            disabled={!canEdit}
            title={term && !canEdit ? `${termLabel(term)} is closed` : undefined}
            onClick={() => setEncoding({})}
          >
            Encode load
          </Button>
          <Button variant="primary" icon={Upload} disabled={!terms.length} onClick={() => setImporting(true)}>
            Import load
          </Button>
        </>
      }
    >
      <div className={panel.body}>
        <section className={`${panel.card} ${panel.grow}`} aria-label="Teaching load">
          <div className={styles.toolbar}>
            <FilterChip<string>
              label="Term"
              value={termId}
              onChange={(id) => {
                setPickedTermId(id)
                setApplied(null)
                setNotice(null)
              }}
              options={terms.map((t) => ({ value: t.id, label: `${termLabel(t)}${t.status === 'Active' ? ' (current)' : ''}` }))}
            />
            <FilterChip<string>
              label="Program"
              value={programId}
              onChange={setProgramId}
              options={[{ value: 'all', label: 'All' }, ...groups.map((g) => ({ value: g.program.id, label: g.program.code }))]}
            />
            <label className={styles.search}>
              <Search size={14} aria-hidden />
              <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search faculty or course…" aria-label="Search the load" />
            </label>
            <span className={styles.counter}>
              {load.data ? `${plural(load.data.facultyCount, 'faculty member', 'faculty')} · ${plural(load.data.courseCount, 'course')}` : ''}
            </span>
          </div>

          {applied && applied.term.id === termId && (
            <div className={styles.banner} role="status">
              Imported {applied.fileName}: {plural(applied.added.length, 'line')} added, {applied.removed.length} removed,{' '}
              {applied.unchanged} unchanged
              {applied.newPeople.length ? ` · ${plural(applied.newPeople.length, 'new person', 'new people')}` : ''}
              {applied.newCourses.length ? ` · ${plural(applied.newCourses.length, 'new course')}` : ''}.
            </div>
          )}
          {notice && notice.termId === termId && (
            <>
              <div className={styles.banner} role="status">
                {notice.text}
              </div>
              {notice.note && <div className={styles.note}>{notice.note}</div>}
            </>
          )}

          <div className={`${panel.headRow} ${styles.head}`}>
            <span className={`${panel.th} ${styles.colPerson}`}>Faculty</span>
            <span className={`${panel.th} ${panel.fill}`}>Courses (sections)</span>
          </div>

          <div className={`${styles.rows} ${load.loading && load.data ? styles.stale : ''}`}>
            {(years.loading || (load.loading && !load.data)) && <Loading compact label="Loading the teaching load…" />}
            {years.error || load.error ? <ErrorState compact error={years.error ?? load.error} onRetry={load.reload} /> : null}
            {visible.map((g) => (
              <div key={g.program.id} className={styles.group}>
                <div className={styles.groupHead}>
                  <span className={styles.groupCode}>{g.program.code}</span>
                  <span className={styles.groupName}>{g.program.name}</span>
                  <span className={styles.groupMeta}>
                    {plural(g.people.length, 'faculty member', 'faculty')} · {plural(g.courseCount, 'course')}
                  </span>
                </div>
                {g.people.map((p) => (
                  <div key={p.person.id} className={styles.row}>
                    <span className={`${styles.person} ${styles.colPerson}`}>
                      <Avatar initials={p.person.initials} size={28} />
                      <span className={styles.personText}>
                        <span className={styles.personName}>{p.person.displayName}</span>
                        <span className={styles.personMeta}>{p.person.externalId}</span>
                      </span>
                    </span>
                    <span className={`${panel.fill} ${styles.courses}`}>
                      {p.courses.map((c) => {
                        const chip = (
                          <>
                            <span className={styles.courseCode}>{c.code}</span>
                            <span className={styles.courseTitle}>{c.title}</span>
                            {c.sections.length > 0 && <span className={styles.sections}>{c.sections.join(', ')}</span>}
                          </>
                        )
                        return canEdit ? (
                          <button
                            key={c.id}
                            type="button"
                            className={`${styles.course} ${styles.courseButton}`}
                            title={`${c.title} · edit`}
                            aria-label={`Edit ${c.code} for ${p.person.displayName}`}
                            onClick={() =>
                              setEncoding({ entry: { person: p.person, programId: g.program.id, course: c, sections: c.sections } })
                            }
                          >
                            {chip}
                          </button>
                        ) : (
                          <span key={c.id} className={styles.course} title={c.title}>
                            {chip}
                          </span>
                        )
                      })}
                      {canEdit && (
                        <button
                          type="button"
                          className={styles.addCourse}
                          title="Add a course"
                          aria-label={`Add a ${g.program.code} course for ${p.person.displayName}`}
                          onClick={() => setEncoding({ preset: { person: p.person, programId: g.program.id } })}
                        >
                          <Plus size={14} aria-hidden />
                        </button>
                      )}
                    </span>
                  </div>
                ))}
              </div>
            ))}
            {load.data && visible.length === 0 && (
              <div className={panel.empty}>
                {load.data.assignments.length
                  ? 'Nobody matches these filters.'
                  : `No teaching load for ${term ? termLabel(term) : 'this term'} yet. Import the registrar’s file, or encode it course by course.`}
              </div>
            )}
          </div>

          <div className={styles.footer}>
            {last
              ? `Last import: ${last.fileName} by ${last.importedBy.displayName}, ${formatDate(last.importedAt)} · ${plural(last.rows, 'row')}, ${last.added} added, ${last.removed} removed`
              : load.data
                ? 'No file imported for this term yet.'
                : ''}
          </div>
        </section>

        <aside className={panel.drawer} aria-label="About importing">
          <div className={panel.drawerHead}>
            <span className={panel.eyebrow}>Importing a load</span>
            <span className={panel.drawerTitle}>One file per semester</span>
            <span className={panel.drawerSub}>CSV or Excel (.xlsx), one row per faculty member × course.</span>
          </div>
          <div className={panel.rule} />
          <div className={`${panel.form} ${styles.drawerScroll}`}>
            <div className={panel.field}>
              <span className={panel.label}>Columns (first row)</span>
              <ul className={styles.columns}>
                {columns.map((c) => (
                  <li key={c.name} className={styles.column}>
                    <code className={styles.columnName}>{c.name}</code>
                    <span className={styles.columnNote}>{c.note}</span>
                    {c.required && <Badge tone="info">Required</Badge>}
                  </li>
                ))}
              </ul>
              <span className={panel.help}>Registrar headings like “Employee No.”, “Subject Code” or “Descriptive Title” work too.</span>
            </div>
            <div className={panel.box}>
              <span className={panel.boxLabel}>What an import does</span>
              <span className={styles.boxText}>
                Replaces the chosen term’s load: lines missing from the file are removed for that term only. New people and
                courses are registered, and each program someone teaches in becomes one of their programs, with a faculty folder.
              </span>
              <span className={styles.boxText}>Documents, folders and other terms are never changed.</span>
            </div>
            <div className={panel.box}>
              <span className={panel.boxLabel}>Encoding by hand</span>
              <span className={styles.boxText}>
                Encode load adds courses one at a time; click a course to change its sections, swap it or remove it. Encoded
                courses work like imported ones, and a later import replaces them along with the rest of the term’s load.
              </span>
            </div>
            <div className={panel.box}>
              <span className={panel.boxLabel}>Where it’s used</span>
              <span className={styles.boxText}>
                The dashboard counts a program’s faculty from the load, and types marked “one submission per course” expect one
                submission per course taught. Faculty pick the course when they submit.
              </span>
            </div>
          </div>
        </aside>
      </div>

      {encoding && term && (
        <TeachingCourseModal
          termId={term.id}
          termName={termLabel(term)}
          assignments={load.data?.assignments ?? []}
          entry={encoding.entry}
          preset={encoding.preset}
          onChanged={changed}
          onClose={() => setEncoding(null)}
        />
      )}
      {importing && (
        <ImportModal terms={terms.filter((t) => t.status !== 'Closed')} initialTermId={term?.status === 'Closed' ? undefined : termId} onDone={done} onClose={() => setImporting(false)} />
      )}
    </SettingsFrame>
  )
}

// ---- import

interface ImportModalProps {
  terms: AcademicTermDto[]
  initialTermId?: string
  onDone: (result: TeachingImportResultDto) => void
  onClose: () => void
}

interface Picked {
  name: string
  base64: string
}

function ImportModal({ terms, initialTermId, onDone, onClose }: ImportModalProps) {
  const [termId, setTermId] = useState(initialTermId ?? terms.find((t) => t.status === 'Active')?.id ?? terms[0]?.id ?? '')
  const [file, setFile] = useState<Picked | null>(null)
  const [fileProblem, setFileProblem] = useState<string | null>(null)
  const [dragging, setDragging] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  const check = useAction((picked: Picked, academicTermId: string) =>
    importTeachingLoad({ academicTermId, fileName: picked.name, contentBase64: picked.base64, apply: false }),
  )
  const apply = useAction((picked: Picked, academicTermId: string) =>
    importTeachingLoad({ academicTermId, fileName: picked.name, contentBase64: picked.base64, apply: true }),
  )
  const [preview, setPreview] = useState<TeachingImportResultDto | null>(null)
  const term = terms.find((t) => t.id === termId)

  const choose = async (picked: File | undefined) => {
    setPreview(null)
    setFileProblem(null)
    apply.clearError()
    if (!picked) return
    if (!/\.(csv|xlsx|tsv|txt)$/i.test(picked.name)) return setFileProblem('Use a .csv or .xlsx file.')
    if (picked.size > MAX_FILE_BYTES) return setFileProblem('Use a file of at most 5 MB.')
    const next = { name: picked.name, base64: await readBase64(picked) }
    setFile(next)
    const result = await check.run(next, termId)
    if (result) setPreview(result)
  }

  const changeTerm = async (id: string) => {
    setTermId(id)
    setPreview(null)
    if (!file) return
    const result = await check.run(file, id)
    if (result) setPreview(result)
  }

  const replace = async () => {
    if (!file || !preview) return
    const result = await apply.run(file, termId)
    if (result) onDone(result)
  }

  const errors = preview?.errors ?? []
  const canApply = Boolean(preview && errors.length === 0 && !apply.pending)
  const changes = preview ? preview.added.length + preview.removed.length + preview.renamedCourses.length + preview.newMemberships.length : 0

  return (
    <Modal
      title="Import teaching load"
      description="Check the registrar’s file first; nothing changes until you replace the term’s load."
      width={720}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" disabled={!canApply} onClick={() => void replace()}>
            {apply.pending ? 'Replacing…' : term ? `Replace ${termLabel(term)} load` : 'Replace load'}
          </Button>
        </>
      }
    >
      <div className={panel.fieldRow}>
        <label className={`${panel.field} ${styles.termField}`}>
          <span className={panel.label}>Term</span>
          <select className={panel.select} value={termId} onChange={(e) => void changeTerm(e.target.value)}>
            {terms.map((t) => (
              <option key={t.id} value={t.id}>
                {termLabel(t)}
                {t.status === 'Active' ? ' (current)' : ''}
              </option>
            ))}
          </select>
        </label>
        <div className={`${panel.field} ${panel.fill}`}>
          <span className={panel.label}>File</span>
          <button
            type="button"
            className={`${styles.drop} ${dragging ? styles.dropActive : ''}`}
            onClick={() => input.current?.click()}
            onDragOver={(e) => {
              e.preventDefault()
              setDragging(true)
            }}
            onDragLeave={() => setDragging(false)}
            onDrop={(e) => {
              e.preventDefault()
              setDragging(false)
              void choose(e.dataTransfer.files[0])
            }}
          >
            <FileSpreadsheet size={18} aria-hidden />
            {file ? (
              <span>
                <strong>{file.name}</strong> · choose another
              </span>
            ) : (
              <span>
                Drop a .csv or .xlsx here, or <strong>choose a file</strong>
              </span>
            )}
          </button>
          <input
            ref={input}
            type="file"
            hidden
            accept=".csv,.xlsx,.tsv,.txt"
            onChange={(e) => {
              void choose(e.target.files?.[0])
              e.target.value = ''
            }}
          />
        </div>
      </div>
      {fileProblem && <span className={panel.error}>{fileProblem}</span>}

      {check.pending && <Loading compact label="Checking the file…" />}
      <InlineError error={check.error} />

      {preview && !check.pending && (
        <div className={styles.preview}>
          <div className={styles.stats}>
            <Stat value={preview.rows} label="rows read" />
            <Stat value={preview.added.length} label="lines added" tone="success" />
            <Stat value={preview.removed.length} label="lines removed" tone={preview.removed.length ? 'warning' : undefined} />
            <Stat value={preview.unchanged} label="unchanged" />
            <Stat value={preview.newPeople.length} label="new people" />
            <Stat value={preview.newCourses.length} label="new courses" />
          </div>

          {errors.length > 0 && (
            <Issues
              tone="danger"
              title={`${plural(errors.length, 'line')} to fix before importing`}
              issues={errors}
            />
          )}
          {preview.warnings.length > 0 && <Issues tone="warning" title={plural(preview.warnings.length, 'warning')} issues={preview.warnings} />}
          {errors.length === 0 && changes === 0 && preview.newPeople.length === 0 && preview.newCourses.length === 0 && (
            <div className={styles.same}>The file matches the current {term ? termLabel(term) : ''} load: nothing would change.</div>
          )}

          <Lines title="Added" lines={preview.added} sign="+" />
          <Lines title="Removed for this term" lines={preview.removed} sign="−" />
          {preview.newPeople.length > 0 && (
            <Section title={`New people (${preview.newPeople.length})`}>
              {preview.newPeople.map((p) => (
                <li key={p.facultyId ?? p.email ?? p.name}>
                  {p.name} <span className={panel.muted}>{[p.facultyId, p.email].filter(Boolean).join(' · ')}</span>
                </li>
              ))}
            </Section>
          )}
          {preview.newCourses.length + preview.renamedCourses.length > 0 && (
            <Section title={`Course catalog (${preview.newCourses.length + preview.renamedCourses.length})`}>
              {preview.newCourses.map((c) => (
                <li key={`${c.program}-${c.code}`}>
                  <Badge tone="success">New</Badge> {c.program} · {c.code} {c.title}
                </li>
              ))}
              {preview.renamedCourses.map((c) => (
                <li key={`${c.program}-${c.code}`}>
                  <Badge tone="info">Renamed</Badge> {c.program} · {c.code} {c.previousTitle} → {c.title}
                </li>
              ))}
            </Section>
          )}
          {preview.newMemberships.length > 0 && (
            <Section title={`Program memberships added (${preview.newMemberships.length})`}>
              {preview.newMemberships.map((m) => (
                <li key={`${m.person}-${m.program}`}>
                  {m.person} joins the {m.program} faculty
                </li>
              ))}
            </Section>
          )}
        </div>
      )}
      <InlineError error={apply.error} />
    </Modal>
  )
}

function Stat({ value, label, tone }: { value: number; label: string; tone?: 'success' | 'warning' }) {
  return (
    <div className={styles.stat}>
      <span className={`${styles.statValue} ${tone && value ? styles[tone] : ''}`}>{value.toLocaleString('en-US')}</span>
      <span className={styles.statLabel}>{label}</span>
    </div>
  )
}

function Issues({ tone, title, issues }: { tone: 'danger' | 'warning'; title: string; issues: ImportIssueDto[] }) {
  const Icon = tone === 'danger' ? CircleAlert : AlertTriangle
  return (
    <div className={`${styles.issues} ${styles[tone]}`} role={tone === 'danger' ? 'alert' : undefined}>
      <span className={styles.issuesTitle}>
        <Icon size={14} aria-hidden /> {title}
      </span>
      <ul>
        {issues.map((issue, i) => (
          <li key={i}>
            {issue.line > 0 && <span className={styles.issueLine}>Row {issue.line}</span>} {issue.message}
          </li>
        ))}
      </ul>
    </div>
  )
}

function Lines({ title, lines, sign }: { title: string; lines: ImportLoadLineDto[]; sign: string }) {
  if (!lines.length) return null
  return (
    <Section title={`${title} (${lines.length})`}>
      {lines.map((l, i) => (
        <li key={i}>
          <span className={styles.sign}>{sign}</span> {l.person} · {l.program} {l.courseCode} {l.courseTitle}
          {l.section ? ` (${l.section})` : ''}
        </li>
      ))}
    </Section>
  )
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <details className={styles.section}>
      <summary>{title}</summary>
      <ul>{children}</ul>
    </details>
  )
}

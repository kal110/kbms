import { useMemo, useState } from 'react'
import { ArrowRight, Lock, Plus, Search, X } from 'lucide-react'
import { ApiError } from '../../api/client'
import { createSubmissionType, listSubmissionTypesForAdmin, updateSubmissionType } from '../../api/settings'
import type { ReviewerRule, SubmissionTypeAdminDto, UpdateSubmissionTypeRequest } from '../../api/types'
import { useAction, useApi } from '../../api/useApi'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { FilterChip } from '../../components/FilterChip'
import { Modal } from '../../components/Modal'
import { Segmented } from '../../components/Segmented'
import { Switch } from '../../components/Switch'
import { SettingsFrame } from './SettingsFrame'
import panel from './panel.module.css'
import styles from './SubmissionTypesPage.module.css'

// Settings → Submission types (frame 20): what people submit, who keeps its required-documents lists
// and who approves it, stage by stage.

type ListFilter = 'all' | 'program' | 'unit'
type StatusFilter = 'all' | 'active' | 'inactive'
type ListKeeper = 'program' | 'unit'

const allRules: ReviewerRule[] = ['UnitHead', 'CollegeDean']

const ruleLabel: Record<ReviewerRule, string> = { UnitHead: 'Unit head', CollegeDean: 'College Dean' }

const ruleHelp: Record<ReviewerRule, string> = {
  UnitHead: 'The program’s Academic Supervisor (the Dean when it has none), or the office’s Head/Director.',
  CollegeDean: 'The Dean of the submitter’s college. Non-academic offices skip this stage.',
}

const keeperOptions: { value: ListKeeper; label: string }[] = [
  { value: 'program', label: 'Each program' },
  { value: 'unit', label: 'Colleges, departments & offices' },
]

const keeperHelp: Record<ListKeeper, string> = {
  program:
    'Every program keeps its own list for this type, set by an administrator or the VPAF. Colleges and departments can add to it; non-academic offices don’t use it.',
  unit: 'No program lists. Colleges, academic departments and non-academic offices keep the lists.',
}

// Same rule as SubmissionTypeService, so mistakes show while typing instead of after Save.
const codeFormat = /^[A-Z][A-Z0-9_]{1,39}$/

function codeProblem(code: string) {
  if (!code) return 'Give the type a code.'
  return codeFormat.test(code) ? null : 'Use 2–40 capital letters, digits or underscores, starting with a letter (e.g. SYLLABUS).'
}

/** "Lab safety checklist" → LAB_SAFETY_CHECKLIST */
function suggestCode(name: string) {
  return name
    .toUpperCase()
    .replace(/[^A-Z0-9\s]/g, '')
    .trim()
    .split(/\s+/)
    .filter(Boolean)
    .join('_')
    .replace(/^[^A-Z]+/, '')
    .slice(0, 40)
}

const plural = (n: number, one: string, many = `${one}s`) => `${n.toLocaleString('en-US')} ${n === 1 ? one : many}`

const sameStages = (a: ReviewerRule[], b: ReviewerRule[]) => a.length === b.length && a.every((r, i) => r === b[i])

const fieldError = (error: unknown, field: string) =>
  error instanceof ApiError ? error.errors?.[field]?.join(' ') : undefined

/** An action error not already shown next to a field. */
const formError = (error: unknown) =>
  error instanceof ApiError && error.errors && Object.keys(error.errors).length ? undefined : error

function StageFlow({ stages }: { stages: ReviewerRule[] }) {
  return (
    <span className={styles.flow}>
      {stages.map((rule, i) => (
        <span key={rule} className={styles.flowStep}>
          {i > 0 && <ArrowRight size={12} className={styles.flowArrow} aria-label="then" />}
          <span className={rule === 'CollegeDean' ? styles.stageDean : styles.stageHead}>{ruleLabel[rule]}</span>
        </span>
      ))}
    </span>
  )
}

function usageLabel(type: SubmissionTypeAdminDto) {
  const { submissions, open } = type.usage
  if (!submissions) return null
  return open ? `${submissions.toLocaleString('en-US')} · ${open} open` : submissions.toLocaleString('en-US')
}

export function SubmissionTypesPage() {
  const types = useApi((signal) => listSubmissionTypesForAdmin(signal), [])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [listFilter, setListFilter] = useState<ListFilter>('all')
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all')
  const [creating, setCreating] = useState(false)

  const all = useMemo(() => types.data ?? [], [types.data])
  const selected = all.find((t) => t.id === selectedId) ?? all[0]
  const activeCount = all.filter((t) => t.active).length

  const visible = useMemo(() => {
    const q = query.trim().toLowerCase()
    return all.filter(
      (t) =>
        (!q || t.name.toLowerCase().includes(q) || t.code.toLowerCase().includes(q)) &&
        (listFilter === 'all' || (listFilter === 'program') === t.programScoped) &&
        (statusFilter === 'all' || (statusFilter === 'active') === t.active),
    )
  }, [all, query, listFilter, statusFilter])

  const replace = (next: SubmissionTypeAdminDto) => types.setData((list) => list?.map((t) => (t.id === next.id ? next : t)))

  const created = (row: SubmissionTypeAdminDto) => {
    types.setData((list) => [...(list ?? []), row])
    setSelectedId(row.id)
    setCreating(false)
  }

  if (!types.data) {
    return (
      <SettingsFrame>
        {types.error ? <ErrorState error={types.error} onRetry={types.reload} /> : <Loading label="Loading submission types…" />}
      </SettingsFrame>
    )
  }

  return (
    <SettingsFrame
      actions={
        <Button variant="primary" icon={Plus} onClick={() => setCreating(true)}>
          New submission type
        </Button>
      }
    >
      <div className={panel.body}>
        <section className={`${panel.card} ${panel.grow}`} aria-label="Submission types">
          <div className={styles.toolbar}>
            <label className={styles.search}>
              <Search size={14} aria-hidden />
              <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search types…" aria-label="Search types" />
            </label>
            <FilterChip<ListFilter>
              label="Lists kept by"
              value={listFilter}
              onChange={setListFilter}
              options={[
                { value: 'all', label: 'Any' },
                { value: 'program', label: 'Each program' },
                { value: 'unit', label: 'Colleges & offices' },
              ]}
            />
            <FilterChip<StatusFilter>
              label="Status"
              value={statusFilter}
              onChange={setStatusFilter}
              options={[
                { value: 'all', label: 'All' },
                { value: 'active', label: 'Active' },
                { value: 'inactive', label: 'Inactive' },
              ]}
            />
            <span className={styles.counter}>
              {activeCount} active · {all.length - activeCount} inactive
            </span>
          </div>

          <div className={`${panel.headRow} ${styles.head}`}>
            <span className={`${panel.th} ${panel.fill}`}>Type</span>
            <span className={`${panel.th} ${styles.colStages}`}>Approval</span>
            <span className={`${panel.th} ${styles.colLists}`}>Lists kept by</span>
            <span className={`${panel.th} ${styles.colUsed}`}>Submissions</span>
            <span className={`${panel.th} ${styles.colStatus}`}>Status</span>
          </div>

          <div className={styles.rows}>
            {visible.map((t) => (
              <button
                key={t.id}
                type="button"
                className={`${styles.row} ${t.id === selected?.id ? styles.selected : ''} ${t.active ? '' : styles.inactive}`}
                aria-pressed={t.id === selected?.id}
                onClick={() => setSelectedId(t.id)}
              >
                <span className={`${styles.name} ${panel.fill}`}>
                  <span className={styles.typeName}>{t.name}</span>
                  <span className={styles.code}>
                    {t.code}
                    {t.description && ` · ${t.description}`}
                  </span>
                </span>
                <span className={`${panel.cell} ${styles.colStages}`}>
                  <StageFlow stages={t.stages.map((s) => s.reviewerRule)} />
                </span>
                <span className={`${panel.cell} ${styles.colLists}`}>
                  {t.programScoped ? 'Each program' : 'Colleges & offices'}
                  <span className={styles.subtle}>
                    {plural(t.programScoped ? t.usage.programLists : t.usage.unitLists, 'list')}
                    {t.perCourse && ' · per course'}
                  </span>
                </span>
                <span className={`${panel.cell} ${styles.colUsed} ${t.usage.submissions ? '' : panel.muted}`}>
                  {usageLabel(t) ?? '—'}
                </span>
                <span className={`${panel.cell} ${styles.colStatus}`}>
                  <Badge tone={t.active ? 'success' : 'neutral'}>{t.active ? 'Active' : 'Inactive'}</Badge>
                </span>
              </button>
            ))}
            {visible.length === 0 && (
              <div className={panel.empty}>{all.length ? 'No submission types match these filters.' : 'No submission types yet.'}</div>
            )}
          </div>

          <div className={styles.footer}>
            Unit head = the program’s Academic Supervisor, or a non-academic office’s Head/Director · College Dean = the
            Dean of the submitter’s college. Nobody reviews their own submission.
          </div>
        </section>

        {selected && <EditDrawer key={selected.id} type={selected} onChange={replace} />}
      </div>

      {creating && <NewTypeModal existing={all} onCreate={created} onClose={() => setCreating(false)} />}
    </SettingsFrame>
  )
}

interface StageEditorProps {
  stages: ReviewerRule[]
  onChange: (stages: ReviewerRule[]) => void
}

/** Approval stages in order. Each reviewer appears once, so there are at most two stages. */
function StageEditor({ stages, onChange }: StageEditorProps) {
  const unused = allRules.filter((r) => !stages.includes(r))
  return (
    <div className={styles.stages}>
      {stages.map((rule, i) => (
        <div key={rule} className={styles.stage}>
          <span className={styles.stageNo}>{i + 1}</span>
          <div className={styles.stageBody}>
            <select
              className={panel.select}
              value={rule}
              aria-label={`Stage ${i + 1} reviewer`}
              onChange={(e) => onChange(stages.map((r, j) => (j === i ? (e.target.value as ReviewerRule) : r)))}
            >
              {allRules
                .filter((r) => r === rule || !stages.includes(r))
                .map((r) => (
                  <option key={r} value={r}>
                    {ruleLabel[r]}
                  </option>
                ))}
            </select>
            <span className={panel.help}>{ruleHelp[rule]}</span>
          </div>
          <button
            type="button"
            className={styles.iconButton}
            disabled={stages.length === 1}
            title={stages.length === 1 ? 'A type needs at least one stage' : undefined}
            aria-label={`Remove stage ${i + 1}`}
            onClick={() => onChange(stages.filter((_, j) => j !== i))}
          >
            <X size={14} />
          </button>
        </div>
      ))}
      {unused.length > 0 && (
        <Button size="sm" icon={Plus} onClick={() => onChange([...stages, unused[0]])} className={styles.addStage}>
          Add a {ruleLabel[unused[0]]} stage
        </Button>
      )}
    </div>
  )
}

interface Draft {
  name: string
  description: string
  keeper: ListKeeper
  perCourse: boolean
  stages: ReviewerRule[]
}

const toDraft = (t: SubmissionTypeAdminDto): Draft => ({
  name: t.name,
  description: t.description ?? '',
  keeper: t.programScoped ? 'program' : 'unit',
  perCourse: t.perCourse,
  stages: t.stages.map((s) => s.reviewerRule),
})

/** Only the changed fields; null leaves a field as it is. */
function changes(type: SubmissionTypeAdminDto, draft: Draft): UpdateSubmissionTypeRequest | null {
  const before = toDraft(type)
  const name = draft.name.trim()
  const description = draft.description.trim()
  const body: UpdateSubmissionTypeRequest = {
    name: name !== before.name ? name : null,
    description: description !== before.description ? description : null,
    programScoped: draft.keeper !== before.keeper ? draft.keeper === 'program' : null,
    active: null,
    stages: sameStages(draft.stages, before.stages) ? null : draft.stages,
    perCourse: draft.perCourse !== before.perCourse ? draft.perCourse : null,
  }
  return Object.values(body).some((v) => v !== null) ? body : null
}

function EditDrawer({ type, onChange }: { type: SubmissionTypeAdminDto; onChange: (type: SubmissionTypeAdminDto) => void }) {
  const [draft, setDraft] = useState(() => toDraft(type))
  const [saved, setSaved] = useState(false)
  const save = useAction((body: UpdateSubmissionTypeRequest) => updateSubmissionType(type.id, body))
  const toggle = useAction((active: boolean) =>
    updateSubmissionType(type.id, { name: null, description: null, programScoped: null, active, stages: null }),
  )

  const body = changes(type, draft)
  const nameMissing = !draft.name.trim()
  const stagesChanged = body?.stages != null
  const { submissions, open, programLists, unitLists } = type.usage
  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => {
    setSaved(false)
    setDraft((d) => ({ ...d, [key]: value }))
  }

  const submit = async () => {
    if (!body || nameMissing) return
    const next = await save.run(body)
    if (!next) return
    onChange(next)
    setDraft(toDraft(next))
    setSaved(true)
  }

  const setActive = async (active: boolean) => {
    const next = await toggle.run(active)
    if (next) onChange(next)
  }

  // Switching who keeps the lists strands the lists the other side keeps; the API refuses it then.
  const strandedLists = draft.keeper !== toDraft(type).keeper ? (type.programScoped ? programLists : unitLists) : 0

  return (
    <aside className={panel.drawer} aria-label="Edit submission type">
      <div className={panel.drawerHead}>
        <span className={panel.eyebrow}>Edit submission type</span>
        <span className={panel.drawerTitle}>{type.name}</span>
        <span className={panel.drawerSub}>
          {type.code} · {plural(submissions, 'submission')}
          {open > 0 && ` · ${open} open`}
        </span>
      </div>
      <div className={panel.rule} />

      <div className={`${panel.form} ${styles.drawerScroll}`}>
        <div className={panel.fieldRow}>
          <label className={`${panel.field} ${panel.fill}`}>
            <span className={panel.label}>Name</span>
            <input
              className={`${panel.input} ${nameMissing || fieldError(save.error, 'name') ? panel.inputInvalid : ''}`}
              value={draft.name}
              maxLength={120}
              onChange={(e) => set('name', e.target.value)}
            />
          </label>
          <div className={panel.field} style={{ width: 170, flexShrink: 0 }}>
            <span className={panel.label}>Code</span>
            <span className={panel.lockedInput} title="Codes can’t change once a type exists">
              <span className={styles.lockedCode}>{type.code}</span>
              <Lock size={12} aria-label="Locked" />
            </span>
          </div>
        </div>
        {nameMissing && <span className={panel.error}>A type needs a name.</span>}
        {fieldError(save.error, 'name') && <span className={panel.error}>{fieldError(save.error, 'name')}</span>}

        <label className={panel.field}>
          <span className={panel.label}>Description</span>
          <textarea
            className={`${panel.input} ${styles.textarea} ${fieldError(save.error, 'description') ? panel.inputInvalid : ''}`}
            value={draft.description}
            maxLength={500}
            rows={2}
            placeholder="What people submit, shown when they start a submission"
            onChange={(e) => set('description', e.target.value)}
          />
          {fieldError(save.error, 'description') && <span className={panel.error}>{fieldError(save.error, 'description')}</span>}
        </label>

        <div className={panel.field}>
          <span className={panel.label}>Required-documents lists kept by</span>
          <Segmented
            options={keeperOptions}
            value={draft.keeper}
            onChange={(v) => {
              set('keeper', v)
              // Courses belong to programs, so only program-kept types can be counted per course.
              if (v === 'unit') set('perCourse', false)
            }}
            ariaLabel="Lists kept by"
          />
          <span className={panel.help}>{keeperHelp[draft.keeper]}</span>
          {strandedLists > 0 && (
            <span className={panel.warn}>
              {type.programScoped
                ? `${plural(programLists, 'program list')} use${programLists === 1 ? 's' : ''} this type. Remove it from those lists first.`
                : 'Programs and non-academic offices can’t share a type: remove it from any office’s list first.'}
            </span>
          )}
        </div>

        <div className={panel.field}>
          <PerCourseSwitch
            checked={draft.perCourse}
            programScoped={draft.keeper === 'program'}
            onChange={(on) => set('perCourse', on)}
          />
          {fieldError(save.error, 'perCourse') && <span className={panel.error}>{fieldError(save.error, 'perCourse')}</span>}
          {draft.perCourse !== type.perCourse && open > 0 && (
            <span className={panel.warn}>
              {plural(open, 'submission')} of this type {open === 1 ? 'is' : 'are'} in review. Change how it’s counted once they’re
              decided.
            </span>
          )}
        </div>

        <div className={panel.field}>
          <span className={panel.label}>Approval stages</span>
          <StageEditor stages={draft.stages} onChange={(stages) => set('stages', stages)} />
          {fieldError(save.error, 'stages') && <span className={panel.error}>{fieldError(save.error, 'stages')}</span>}
          {stagesChanged && open > 0 && (
            <span className={panel.warn}>
              {plural(open, 'open submission')} keep their current stage number and continue with these stages.
            </span>
          )}
        </div>

        <div className={panel.box}>
          <span className={panel.boxLabel}>In use</span>
          <span className={panel.boxLine}>
            {submissions ? `${plural(submissions, 'submission')}, ${open} still open` : 'No submissions yet.'}
          </span>
          <span className={panel.boxLine}>
            {type.programScoped
              ? `Listed by ${plural(programLists, 'program')}${unitLists ? ` and ${plural(unitLists, 'college or department', 'colleges or departments')}` : ''}`
              : unitLists
                ? `Listed by ${plural(unitLists, 'college, department or office', 'colleges, departments or offices')}`
                : 'Not on any published list yet.'}
          </span>
        </div>

        <div className={panel.toggleRow} style={{ alignItems: 'center' }}>
          <Switch checked={type.active} disabled={toggle.pending} onChange={(on) => void setActive(on)} label="Active" />
          <span>
            {type.active
              ? 'Active: offered when people start a submission'
              : 'Inactive: hidden from new submissions; open ones carry on'}
          </span>
        </div>
      </div>

      {formError(save.error) || toggle.error ? (
        <div className={styles.actionError}>
          <InlineError error={formError(save.error) ?? toggle.error} />
        </div>
      ) : null}
      <div className={panel.drawerFooter}>
        <Button onClick={() => setDraft(toDraft(type))} disabled={!body || save.pending}>
          Discard
        </Button>
        <span className={styles.footerActions}>
          {saved && !body && <span className={styles.saved}>Saved</span>}
          <Button variant="primary" disabled={!body || nameMissing || save.pending} onClick={() => void submit()}>
            {save.pending ? 'Saving…' : 'Save changes'}
          </Button>
        </span>
      </div>
    </aside>
  )
}

interface PerCourseSwitchProps {
  checked: boolean
  programScoped: boolean
  onChange: (on: boolean) => void
}

/** "One submission per course": faculty pick a course from their teaching load for each submission. */
function PerCourseSwitch({ checked, programScoped, onChange }: PerCourseSwitchProps) {
  return (
    <div className={panel.toggleRow}>
      <Switch checked={checked && programScoped} disabled={!programScoped} onChange={onChange} label="One submission per course" />
      <span className={panel.toggleText}>
        <span className={panel.toggleTitle}>One submission per course</span>
        <span className={panel.help}>
          {!programScoped
            ? 'Only for types each program keeps a list for: courses belong to programs.'
            : checked
              ? 'Faculty pick a course from their teaching load, and each course they teach is expected once per term (syllabi, lab manuals).'
              : 'One per person per term. Turn on for documents every course needs, like a syllabus.'}
        </span>
      </span>
    </div>
  )
}

interface NewTypeModalProps {
  existing: SubmissionTypeAdminDto[]
  onCreate: (row: SubmissionTypeAdminDto) => void
  onClose: () => void
}

function NewTypeModal({ existing, onCreate, onClose }: NewTypeModalProps) {
  const [name, setName] = useState('')
  const [code, setCode] = useState('')
  const [codeTouched, setCodeTouched] = useState(false)
  const [description, setDescription] = useState('')
  const [keeper, setKeeper] = useState<ListKeeper | null>(null)
  const [perCourse, setPerCourse] = useState(false)
  const [stages, setStages] = useState<ReviewerRule[]>(['UnitHead'])
  const [submitted, setSubmitted] = useState(false)
  const create = useAction(createSubmissionType)

  const effectiveCode = codeTouched ? code : suggestCode(name)
  const duplicate = existing.some((t) => t.code === effectiveCode)
  const errors = {
    name: !name.trim() ? 'Give the type a name.' : null,
    code: codeProblem(effectiveCode) ?? (duplicate ? `${effectiveCode} is already used.` : null),
    keeper: !keeper ? 'Pick who keeps the required-documents lists.' : null,
  }
  const valid = !errors.name && !errors.code && !errors.keeper

  const submit = async () => {
    setSubmitted(true)
    if (!valid || !keeper) return
    const row = await create.run({
      code: effectiveCode,
      name: name.trim(),
      description: description.trim() || null,
      programScoped: keeper === 'program',
      stages,
      perCourse: keeper === 'program' && perCourse,
    })
    if (row) onCreate(row)
  }

  const showError = (key: keyof typeof errors, field: string) =>
    (submitted && errors[key]) || fieldError(create.error, field) || null

  return (
    <Modal
      title="New submission type"
      description="What people submit for review. Required-documents lists then say which documents it needs."
      onClose={onClose}
      width={560}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" onClick={() => void submit()} disabled={(submitted && !valid) || create.pending}>
            {create.pending ? 'Creating…' : 'Create type'}
          </Button>
        </>
      }
    >
      <div className={panel.fieldRow}>
        <label className={`${panel.field} ${panel.fill}`}>
          <span className={panel.label}>Name</span>
          <input
            className={`${panel.input} ${showError('name', 'name') ? panel.inputInvalid : ''}`}
            value={name}
            autoFocus
            maxLength={120}
            placeholder="e.g. Course portfolio"
            onChange={(e) => setName(e.target.value)}
          />
          {showError('name', 'name') && <span className={panel.error}>{showError('name', 'name')}</span>}
        </label>
        <label className={panel.field} style={{ width: 190, flexShrink: 0 }}>
          <span className={panel.label}>Code</span>
          <input
            className={`${panel.input} ${showError('code', 'code') ? panel.inputInvalid : ''}`}
            value={effectiveCode}
            maxLength={40}
            onChange={(e) => {
              setCodeTouched(true)
              setCode(e.target.value.toUpperCase().replace(/\s+/g, '_'))
            }}
          />
          {showError('code', 'code') ? (
            <span className={panel.error}>{showError('code', 'code')}</span>
          ) : (
            <span className={panel.help}>Can’t change later.</span>
          )}
        </label>
      </div>

      <label className={panel.field}>
        <span className={panel.label}>Description (optional)</span>
        <textarea
          className={`${panel.input} ${styles.textarea}`}
          value={description}
          maxLength={500}
          rows={2}
          placeholder="Shown when people start a submission"
          onChange={(e) => setDescription(e.target.value)}
        />
      </label>

      <div className={panel.field}>
        <span className={panel.label}>Required-documents lists kept by</span>
        <Segmented options={keeperOptions} value={keeper} onChange={setKeeper} ariaLabel="Lists kept by" />
        {submitted && errors.keeper ? (
          <span className={panel.error}>{errors.keeper}</span>
        ) : (
          <span className={panel.help}>{keeper ? keeperHelp[keeper] : 'Programs (faculty submissions) or colleges and offices.'}</span>
        )}
      </div>

      <PerCourseSwitch checked={perCourse} programScoped={keeper === 'program'} onChange={setPerCourse} />

      <div className={panel.field}>
        <span className={panel.label}>Approval stages</span>
        <StageEditor stages={stages} onChange={setStages} />
        {fieldError(create.error, 'stages') && <span className={panel.error}>{fieldError(create.error, 'stages')}</span>}
      </div>
      <InlineError error={formError(create.error)} />
    </Modal>
  )
}

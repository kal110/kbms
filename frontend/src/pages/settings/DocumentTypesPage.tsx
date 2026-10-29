import { useEffect, useMemo, useState } from 'react'
import { ListChecks, Lock, Plus, Search } from 'lucide-react'
import { ApiError } from '../../api/client'
import { getActiveTerm, termLabel } from '../../api/reference'
import {
  createDocumentType,
  listDocumentTypesForAdmin,
  listRetentionPolicies,
  reactivateDocumentType,
  retireDocumentType,
  updateDocumentType,
} from '../../api/settings'
import type {
  AcademicTag,
  AcademicTermDto,
  DocumentTypeAdminDto,
  DocumentTypeUsageDto,
  RequirementScope,
  RetentionPolicyDto,
  TypeScope,
  UpdateDocumentTypeRequest,
} from '../../api/types'
import { useAction, useApi } from '../../api/useApi'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { Modal } from '../../components/Modal'
import { Switch } from '../../components/Switch'
import { FilterChip } from '../../components/FilterChip'
import { Segmented } from '../../components/Segmented'
import { SettingsFrame } from './SettingsFrame'
import panel from './panel.module.css'
import styles from './DocumentTypesPage.module.css'

type TagFilter = 'any' | AcademicTag
type StatusFilter = 'all' | 'active' | 'retired'

const tagOptions: { value: AcademicTag; label: string }[] = [
  { value: 'None', label: 'None' },
  { value: 'Year', label: 'Academic year' },
  { value: 'Term', label: 'Term' },
]

const tagLabel: Record<AcademicTag, string> = { None: 'None', Year: 'Academic year', Term: 'Term' }

// Same rules as DocumentTypeService, so mistakes show while typing instead of after Save.
const codeFormat = /^[A-Z][A-Z0-9]{0,11}$/
const patternFormat = /^(?:[A-Z0-9\-_/.]|\{yyyy\}|\{yy\}|\{seq:[1-9]\})+$/

function codeProblem(code: string) {
  if (!code) return 'Give the type a short code.'
  return codeFormat.test(code) ? null : 'Use 1–12 capital letters or digits, starting with a letter (e.g. SYL).'
}

function patternProblem(pattern: string) {
  if (!pattern) return null
  if (pattern.length > 60 || !patternFormat.test(pattern) || !pattern.includes('{seq:')) {
    return 'Use capitals, digits, - _ / . and the tokens {yyyy}, {yy} and {seq:N}; {seq:N} is required.'
  }
  return null
}

/** What the pattern produces, e.g. SYL-{yyyy}-{seq:4} → SYL-2026-0001. The API numbers by the year a number is issued. */
function sampleNumber(pattern: string) {
  if (!pattern.trim()) return null
  const year = String(new Date().getFullYear())
  return pattern
    .replaceAll('{yyyy}', year)
    .replaceAll('{yy}', year.slice(2))
    .replace(/\{seq:(\d)\}/g, (_, digits: string) => '1'.padStart(Number(digits), '0'))
}

function patternHelp(pattern: string) {
  const sample = sampleNumber(pattern)
  if (!sample) return 'Documents of this type get no number.'
  return `Format: ${sample} — {yyyy} is the year the number is issued; {seq:N} counts up within that year.`
}

function tagHelp(tag: AcademicTag, name: string, term: AcademicTermDto | null | undefined) {
  const noun = name.trim() ? name.trim().toLowerCase() : 'document of this type'
  if (tag === 'Term') {
    const example = term ? `, e.g. ${termLabel(term)}` : ''
    return `Every ${noun} must carry a term${example}. The academic year comes from the term, so nobody picks it twice.`
  }
  if (tag === 'Year') return `Every ${noun} must carry an academic year${term ? `, e.g. ${term.academicYearName}` : ''}.`
  return 'No year or term. Use this for documents that don’t belong to a period, like policies and board resolutions.'
}

const scopeNoun: Record<RequirementScope, [string, string]> = {
  College: ['college', 'colleges'],
  Department: ['department', 'departments'],
  Program: ['program', 'programs'],
}

/** The "Used by" column: how many units' published checklists include the type, else how many drafts do. */
function usageSummary(type: DocumentTypeAdminDto) {
  const published = new Map(type.usedBy.filter((u) => !u.draft).map((u) => [u.scopeId, u.scopeType]))
  if (published.size) {
    const kinds = [...new Set(published.values())]
    const n = published.size
    return { label: kinds.length === 1 ? `${n} ${scopeNoun[kinds[0]][n === 1 ? 0 : 1]}` : `${n} units`, draft: false }
  }
  const drafts = new Set(type.usedBy.map((u) => u.scopeId))
  return drafts.size ? { label: `${drafts.size} (draft)`, draft: true } : null
}

const rule = (required: boolean) => (required ? 'required' : 'optional')

/** "BSCS, BSIT → “Course syllabus” · required": units that list the type the same way share a line. */
function usageLines(usedBy: DocumentTypeUsageDto[]) {
  const groups = new Map<string, { scopes: string[]; submissionType: string; required: boolean; draft: boolean }>()
  for (const u of usedBy) {
    const key = `${u.submissionTypeName}|${u.required}|${u.draft}`
    const group = groups.get(key) ?? { scopes: [], submissionType: u.submissionTypeName, required: u.required, draft: u.draft }
    const scope = u.draft ? `${u.scopeCode} draft v${u.setVersion}` : u.scopeCode
    if (!group.scopes.includes(scope)) group.scopes.push(scope)
    groups.set(key, group)
  }
  const lines = new Map<string, { draft: boolean; items: { submissionType: string; required: boolean }[] }>()
  for (const g of groups.values()) {
    const who = g.scopes.join(', ')
    const line = lines.get(who) ?? { draft: g.draft, items: [] }
    line.items.push(g)
    lines.set(who, line)
  }
  return [...lines].map(([who, { draft, items }]) => {
    const sameRule = items.every((i) => i.required === items[0].required)
    const what = sameRule
      ? `${items.map((i) => `“${i.submissionType}”`).join(', ')} · ${rule(items[0].required)}`
      : items.map((i) => `“${i.submissionType}” · ${rule(i.required)}`).join(', ')
    return { text: `${who} → ${what}`, draft }
  })
}

// Active types by name, then retired ones (the API's order), kept after local saves.
const byStatusThenName = (a: DocumentTypeAdminDto, b: DocumentTypeAdminDto) =>
  Number(b.active) - Number(a.active) || a.name.localeCompare(b.name)

function TagPill({ tag }: { tag: AcademicTag }) {
  const tone = tag === 'Term' ? styles.tagTerm : tag === 'Year' ? styles.tagYear : styles.tagNone
  return <span className={`${styles.tag} ${tone}`}>{tagLabel[tag]}</span>
}

const fieldError = (error: unknown, field: string) =>
  error instanceof ApiError ? error.errors?.[field]?.join(' ') : undefined

/** An action error not already shown next to a field. */
const formError = (error: unknown) =>
  error instanceof ApiError && error.errors && Object.keys(error.errors).length ? undefined : error

export function DocumentTypesPage() {
  const types = useApi((signal) => listDocumentTypesForAdmin(signal), [])
  const policies = useApi((signal) => listRetentionPolicies(signal), [])
  // Only for the tag help's example; a missing active term just drops the example.
  const activeTerm = useApi(() => getActiveTerm().catch(() => null), [])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [tagFilter, setTagFilter] = useState<TagFilter>('any')
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all')
  const [creating, setCreating] = useState(false)

  const all = useMemo(() => [...(types.data ?? [])].sort(byStatusThenName), [types.data])
  const selected = all.find((t) => t.id === selectedId) ?? all[0]
  const activeCount = all.filter((t) => t.active).length
  const retiredCount = all.length - activeCount

  const visible = useMemo(() => {
    const q = query.trim().toLowerCase()
    return all.filter(
      (t) =>
        (!q || t.name.toLowerCase().includes(q) || t.code.toLowerCase().includes(q)) &&
        (tagFilter === 'any' || t.tag === tagFilter) &&
        (statusFilter === 'all' || (statusFilter === 'active') === t.active),
    )
  }, [all, query, tagFilter, statusFilter])

  const replace = (next: DocumentTypeAdminDto) =>
    types.setData((list) => list?.map((t) => (t.id === next.id ? next : t)))

  const created = (row: DocumentTypeAdminDto) => {
    types.setData((list) => [...(list ?? []), row])
    setSelectedId(row.id)
    setCreating(false)
  }

  if (!types.data) {
    return (
      <SettingsFrame>
        {types.error ? <ErrorState error={types.error} onRetry={types.reload} /> : <Loading label="Loading document types…" />}
      </SettingsFrame>
    )
  }

  return (
    <SettingsFrame
      actions={
        <Button variant="primary" icon={Plus} onClick={() => setCreating(true)}>
          New document type
        </Button>
      }
    >
      <div className={panel.body}>
        <section className={`${panel.card} ${panel.grow}`} aria-label="Document types">
          <div className={styles.toolbar}>
            <label className={styles.search}>
              <Search size={14} aria-hidden />
              <input
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder="Search types…"
                aria-label="Search types"
              />
            </label>
            <FilterChip<TagFilter>
              label="Academic tag"
              value={tagFilter}
              onChange={setTagFilter}
              options={[{ value: 'any', label: 'Any' }, ...tagOptions]}
            />
            <FilterChip<StatusFilter>
              label="Status"
              value={statusFilter}
              onChange={setStatusFilter}
              options={[
                { value: 'all', label: 'All' },
                { value: 'active', label: 'Active' },
                { value: 'retired', label: 'Retired' },
              ]}
            />
            <span className={styles.counter}>
              {activeCount} active · {retiredCount} retired
            </span>
          </div>
          {types.error ? <ErrorState compact error={types.error} onRetry={types.reload} /> : null}

          <div className={`${panel.headRow} ${styles.head}`}>
            <span className={`${panel.th} ${panel.fill}`}>Type</span>
            <span className={`${panel.th} ${styles.colTag}`}>Academic tag</span>
            <span className={`${panel.th} ${styles.colPattern}`}>Number pattern</span>
            <span className={`${panel.th} ${styles.colUsed}`}>Used by</span>
            <span className={`${panel.th} ${styles.colDocs}`}>Docs</span>
            <span className={`${panel.th} ${styles.colStatus}`}>Status</span>
          </div>

          <div className={styles.rows}>
            {visible.map((t) => {
              const unused = t.active && t.documentCount === 0
              const usage = usageSummary(t)
              return (
                <button
                  key={t.id}
                  type="button"
                  className={`${styles.row} ${t.id === selected?.id ? styles.selected : ''} ${t.active ? '' : styles.retired}`}
                  aria-pressed={t.id === selected?.id}
                  onClick={() => setSelectedId(t.id)}
                >
                  <span className={`${styles.name} ${panel.fill}`}>
                    <span className={styles.typeName}>{t.name}</span>
                    <span className={`${styles.code} ${unused ? styles.codeNew : ''}`}>
                      {t.code}
                      {unused && ' · no documents yet'}
                      {t.replacedByName && ` · replaced by ${t.replacedByName}`}
                    </span>
                  </span>
                  <span className={`${panel.cell} ${styles.colTag}`}>
                    <TagPill tag={t.tag} />
                  </span>
                  <span className={`${panel.cell} ${styles.colPattern} ${t.numberPattern && t.active ? '' : panel.muted}`}>
                    {t.active ? t.numberPattern || '— (no number)' : '—'}
                  </span>
                  <span className={`${panel.cell} ${styles.colUsed} ${usage?.draft ? styles.draftUse : usage ? '' : panel.muted}`}>
                    {usage?.label ?? '—'}
                  </span>
                  <span className={`${panel.cell} ${styles.colDocs} ${t.documentCount ? '' : panel.muted}`}>
                    {t.documentCount.toLocaleString('en-US')}
                  </span>
                  <span className={`${panel.cell} ${styles.colStatus}`}>
                    <Badge tone={t.active ? 'success' : 'neutral'}>{t.active ? 'Active' : 'Retired'}</Badge>
                  </span>
                </button>
              )
            })}
            {visible.length === 0 && (
              <div className={panel.empty}>{all.length ? 'No document types match these filters.' : 'No document types yet.'}</div>
            )}
          </div>

          <div className={styles.footer}>
            Academic tag: None = no year or term · Academic year = needs a year · Term = needs a term (the year comes
            from the term)
          </div>
        </section>

        {selected && (
          <EditDrawer
            key={selected.id}
            type={selected}
            types={all}
            policies={policies.data}
            policiesError={policies.error}
            term={activeTerm.data}
            onChange={replace}
            onReload={types.reload}
          />
        )}
      </div>

      {creating && (
        <NewTypeModal
          existing={all}
          policies={policies.data ?? []}
          term={activeTerm.data}
          onCreate={created}
          onClose={() => setCreating(false)}
        />
      )}
    </SettingsFrame>
  )
}

interface Draft {
  name: string
  code: string
  tag: AcademicTag
  numberPattern: string
  retentionPolicyId: string
}

const toDraft = (t: DocumentTypeAdminDto): Draft => ({
  name: t.name,
  code: t.code,
  tag: t.tag,
  numberPattern: t.numberPattern ?? '',
  retentionPolicyId: t.retention?.id ?? '',
})

/** Only the changed fields; null leaves a field as it is. */
function changes(type: DocumentTypeAdminDto, draft: Draft): UpdateDocumentTypeRequest | null {
  const before = toDraft(type)
  const name = draft.name.trim()
  const code = draft.code.trim().toUpperCase()
  const pattern = draft.numberPattern.trim()
  const body: UpdateDocumentTypeRequest = {
    name: name !== before.name ? name : null,
    code: code !== before.code ? code : null,
    description: null,
    numberPattern: pattern !== before.numberPattern ? pattern : null,
    tag: draft.tag !== before.tag ? draft.tag : null,
    scope: null,
    retentionPolicyId: draft.retentionPolicyId && draft.retentionPolicyId !== before.retentionPolicyId ? draft.retentionPolicyId : null,
    clearRetentionPolicy: !draft.retentionPolicyId && before.retentionPolicyId ? true : null,
  }
  return Object.values(body).some((v) => v !== null) ? body : null
}

interface EditDrawerProps {
  type: DocumentTypeAdminDto
  types: DocumentTypeAdminDto[]
  policies: RetentionPolicyDto[] | undefined
  policiesError: unknown
  term: AcademicTermDto | null | undefined
  onChange: (type: DocumentTypeAdminDto) => void
  onReload: () => void
}

function EditDrawer({ type, types, policies, policiesError, term, onChange, onReload }: EditDrawerProps) {
  const [draft, setDraft] = useState(() => toDraft(type))
  const [confirmRetire, setConfirmRetire] = useState(false)
  const [saved, setSaved] = useState(false)
  const save = useAction((body: UpdateDocumentTypeRequest) => updateDocumentType(type.id, type.etag, body))
  const reactivate = useAction(() => reactivateDocumentType(type.id, type.etag))

  useEffect(() => {
    if (!saved) return
    const id = window.setTimeout(() => setSaved(false), 2000)
    return () => window.clearTimeout(id)
  }, [saved])

  const body = changes(type, draft)
  const nameMissing = !draft.name.trim()
  const codeError = type.codeLocked ? null : codeProblem(draft.code.trim().toUpperCase())
  const patternError = patternProblem(draft.numberPattern.trim())
  const usage = usageSummary(type)
  const lines = usageLines(type.usedBy)
  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => setDraft((d) => ({ ...d, [key]: value }))

  // The current policy stays selectable even when the list failed to load.
  const policyOptions = policies ?? (type.retention ? [type.retention] : [])

  const submit = async () => {
    if (!body || nameMissing || codeError || patternError) return
    setSaved(false)
    const next = await save.run(body)
    if (!next) return
    onChange(next)
    setDraft(toDraft(next))
    setSaved(true)
  }

  const restore = async () => {
    const next = await reactivate.run()
    if (next) onChange(next)
  }

  const stale = [save.error, reactivate.error].some((e) => e instanceof ApiError && e.isStale)

  return (
    <aside className={panel.drawer} aria-label="Edit document type">
      <div className={panel.drawerHead}>
        <span className={panel.eyebrow}>Edit document type</span>
        <span className={panel.drawerTitle}>{type.name}</span>
        <span className={panel.drawerSub}>
          {type.code} · {type.documentCount.toLocaleString('en-US')} documents
          {usage && ` · used by ${usage.label}`}
        </span>
      </div>
      <div className={panel.rule} />

      <div className={`${panel.form} ${styles.drawerScroll}`}>
        <label className={panel.field}>
          <span className={panel.label}>Name</span>
          <input
            className={`${panel.input} ${nameMissing || fieldError(save.error, 'name') ? panel.inputInvalid : ''}`}
            value={draft.name}
            maxLength={120}
            onChange={(e) => set('name', e.target.value)}
          />
          {nameMissing && <span className={panel.error}>A type needs a name.</span>}
          {fieldError(save.error, 'name') && <span className={panel.error}>{fieldError(save.error, 'name')}</span>}
        </label>

        <div className={panel.fieldRow}>
          <label className={panel.field} style={{ width: 100, flexShrink: 0 }}>
            <span className={panel.label}>Code</span>
            {type.codeLocked ? (
              <span className={panel.lockedInput} title="Locked once documents use this type">
                {type.code}
                <Lock size={12} aria-label="Locked" />
              </span>
            ) : (
              <input
                className={`${panel.input} ${codeError || fieldError(save.error, 'code') ? panel.inputInvalid : ''}`}
                value={draft.code}
                maxLength={12}
                onChange={(e) => set('code', e.target.value.toUpperCase())}
              />
            )}
          </label>
          <label className={`${panel.field} ${panel.fill}`}>
            <span className={panel.label}>Retention</span>
            <select
              className={panel.select}
              value={draft.retentionPolicyId}
              onChange={(e) => set('retentionPolicyId', e.target.value)}
            >
              <option value="">No retention rule</option>
              {policyOptions.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          </label>
        </div>
        {(codeError || fieldError(save.error, 'code')) && (
          <span className={panel.error}>{codeError ?? fieldError(save.error, 'code')}</span>
        )}
        {policiesError ? <InlineError error={policiesError} /> : null}

        <div className={panel.field}>
          <span className={panel.label}>Academic tag (required on every document of this type)</span>
          <Segmented
            options={tagOptions}
            value={draft.tag}
            onChange={(v) => set('tag', v)}
            disabled={type.codeLocked}
            ariaLabel="Academic tag"
          />
          <span className={panel.help}>{tagHelp(draft.tag, draft.name, term)}</span>
          {type.codeLocked && (
            <span className={panel.help}>
              <Lock size={11} aria-hidden /> Code and tag are locked: {type.documentCount.toLocaleString('en-US')}{' '}
              {type.documentCount === 1 ? 'document uses' : 'documents use'} this type. Create a new type to change them.
            </span>
          )}
        </div>

        <label className={panel.field}>
          <span className={panel.label}>Number pattern</span>
          <input
            className={`${panel.input} ${patternError || fieldError(save.error, 'numberPattern') ? panel.inputInvalid : ''}`}
            value={draft.numberPattern}
            maxLength={60}
            placeholder="Leave empty for no document number"
            onChange={(e) => set('numberPattern', e.target.value)}
          />
          {patternError || fieldError(save.error, 'numberPattern') ? (
            <span className={panel.error}>{patternError ?? fieldError(save.error, 'numberPattern')}</span>
          ) : (
            <span className={panel.help}>{patternHelp(draft.numberPattern)}</span>
          )}
        </label>

        <div className={panel.box}>
          <span className={panel.boxLabel}>Required in checklists</span>
          {lines.length ? (
            lines.map((line) => (
              <span key={line.text} className={`${panel.boxLine} ${line.draft ? styles.draftUse : ''}`}>
                <ListChecks size={14} className={styles.usedIcon} aria-hidden />
                {line.text}
              </span>
            ))
          ) : (
            <span className={`${panel.boxLine} ${panel.muted}`}>Not required in any checklist yet.</span>
          )}
        </div>

        <div className={panel.toggleRow} style={{ alignItems: 'center' }}>
          <Switch
            checked={type.active}
            disabled={reactivate.pending}
            onChange={(on) => (on ? void restore() : setConfirmRetire(true))}
            label="Active"
          />
          <span>
            {type.active ? 'Active — can be chosen for new documents' : 'Retired — hidden from pickers; documents keep it'}
          </span>
        </div>
      </div>

      {formError(save.error) || reactivate.error ? (
        <div className={styles.actionError}>
          <InlineError error={formError(save.error) ?? reactivate.error} />
          {stale && (
            <Button size="sm" onClick={onReload}>
              Reload this type
            </Button>
          )}
        </div>
      ) : null}
      <div className={panel.drawerFooter}>
        {type.active ? (
          <Button onClick={() => setConfirmRetire(true)}>Retire type…</Button>
        ) : (
          <Button onClick={() => void restore()} disabled={reactivate.pending}>
            {reactivate.pending ? 'Restoring…' : 'Restore type'}
          </Button>
        )}
        <span className={styles.footerActions}>
          {saved && <span className={styles.saved}>Saved</span>}
          <Button
            variant="primary"
            disabled={!body || nameMissing || Boolean(codeError) || Boolean(patternError) || save.pending}
            onClick={() => void submit()}
          >
            {save.pending ? 'Saving…' : 'Save changes'}
          </Button>
        </span>
      </div>

      {confirmRetire && (
        <RetireModal
          type={type}
          types={types}
          lines={lines}
          onRetired={(next) => {
            onChange(next)
            setConfirmRetire(false)
          }}
          onClose={() => setConfirmRetire(false)}
        />
      )}
    </aside>
  )
}

interface RetireModalProps {
  type: DocumentTypeAdminDto
  types: DocumentTypeAdminDto[]
  lines: { text: string; draft: boolean }[]
  onRetired: (type: DocumentTypeAdminDto) => void
  onClose: () => void
}

function RetireModal({ type, types, lines, onRetired, onClose }: RetireModalProps) {
  const [replacementId, setReplacementId] = useState('')
  const retire = useAction(() => retireDocumentType(type.id, type.etag, replacementId || null))
  const candidates = types.filter((t) => t.active && t.id !== type.id)

  const confirm = async () => {
    const next = await retire.run()
    if (next) onRetired(next)
  }

  return (
    <Modal
      title={`Retire “${type.name}”?`}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="danger" onClick={() => void confirm()} disabled={retire.pending}>
            {retire.pending ? 'Retiring…' : 'Retire type'}
          </Button>
        </>
      }
    >
      <p className={panel.help}>
        {type.documentCount > 0
          ? `A type in use is never deleted. Retiring hides it from pickers; the ${type.documentCount.toLocaleString('en-US')} ${type.documentCount === 1 ? 'document that uses' : 'documents that use'} it keep it and stay searchable.`
          : 'Retiring hides it from pickers. No documents use it yet.'}{' '}
        The code {type.code} stays reserved.
      </p>
      {lines.length > 0 && (
        <p className={panel.warn}>
          Still listed in: {lines.map((l) => l.text).join('; ')}. Those checklists need another type.
        </p>
      )}
      <label className={panel.field}>
        <span className={panel.label}>Replaced by (optional)</span>
        <select className={panel.select} value={replacementId} onChange={(e) => setReplacementId(e.target.value)}>
          <option value="">No replacement</option>
          {candidates.map((t) => (
            <option key={t.id} value={t.id}>
              {t.name} ({t.code})
            </option>
          ))}
        </select>
        <span className={panel.help}>Shown next to the retired type so people know what to use instead.</span>
      </label>
      <InlineError error={retire.error} />
    </Modal>
  )
}

function suggestCode(name: string) {
  const words = name
    .toUpperCase()
    .replace(/[^A-Z0-9\s]/g, '')
    .split(/\s+/)
    .filter(Boolean)
  if (words.length === 0) return ''
  if (words.length === 1) return words[0].slice(0, 3)
  return words
    .slice(0, 4)
    .map((w) => w[0])
    .join('')
}

interface NewTypeModalProps {
  existing: DocumentTypeAdminDto[]
  policies: RetentionPolicyDto[]
  term: AcademicTermDto | null | undefined
  onCreate: (row: DocumentTypeAdminDto) => void
  onClose: () => void
}

function NewTypeModal({ existing, policies, term, onCreate, onClose }: NewTypeModalProps) {
  const [name, setName] = useState('')
  const [code, setCode] = useState('')
  const [codeTouched, setCodeTouched] = useState(false)
  const [tag, setTag] = useState<AcademicTag | null>(null)
  const [scope, setScope] = useState<TypeScope>('All')
  const [pattern, setPattern] = useState('')
  const [patternTouched, setPatternTouched] = useState(false)
  const [retentionId, setRetentionId] = useState<string | null>(null)
  const [submitted, setSubmitted] = useState(false)
  const create = useAction(createDocumentType)

  const defaultPolicy = policies.find((p) => p.name === '5 years after the term') ?? policies[0]
  const effectiveRetention = retentionId ?? defaultPolicy?.id ?? ''
  const effectiveCode = codeTouched ? code : suggestCode(name)
  const effectivePattern = patternTouched ? pattern : effectiveCode ? `${effectiveCode}-{yyyy}-{seq:4}` : ''
  const duplicate = existing.some((t) => t.code === effectiveCode)
  const errors = {
    name: !name.trim() ? 'Give the type a name.' : null,
    code: codeProblem(effectiveCode) ?? (duplicate ? `${effectiveCode} is already used.` : null),
    tag: !tag ? 'Pick an academic tag. Every document of this type will need it.' : null,
    pattern: patternProblem(effectivePattern.trim()),
  }
  const valid = !errors.name && !errors.code && !errors.tag && !errors.pattern
  const serverCodeTaken = create.error instanceof ApiError && create.error.code === 'code-taken'

  const submit = async () => {
    setSubmitted(true)
    if (!valid || !tag) return
    const row = await create.run({
      code: effectiveCode,
      name: name.trim(),
      description: null,
      numberPattern: effectivePattern.trim() || null,
      tag,
      scope,
      retentionPolicyId: effectiveRetention || null,
    })
    if (row) onCreate(row)
  }

  const showError = (key: keyof typeof errors, field: string) =>
    (submitted && errors[key]) || fieldError(create.error, field) || null

  return (
    <Modal
      title="New document type"
      description="Every document must carry a type. The type decides the academic tag, number and retention."
      onClose={onClose}
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
            placeholder="e.g. Lab safety checklist"
            onChange={(e) => setName(e.target.value)}
          />
          {showError('name', 'name') && <span className={panel.error}>{showError('name', 'name')}</span>}
        </label>
        <label className={panel.field} style={{ width: 110, flexShrink: 0 }}>
          <span className={panel.label}>Code</span>
          <input
            className={`${panel.input} ${showError('code', 'code') || serverCodeTaken ? panel.inputInvalid : ''}`}
            value={effectiveCode}
            maxLength={12}
            onChange={(e) => {
              setCodeTouched(true)
              setCode(e.target.value.toUpperCase())
            }}
          />
          {showError('code', 'code') && <span className={panel.error}>{showError('code', 'code')}</span>}
        </label>
      </div>

      <div className={panel.field}>
        <span className={panel.label}>Academic tag (required on every document of this type)</span>
        <Segmented options={tagOptions} value={tag} onChange={setTag} ariaLabel="Academic tag" />
        {submitted && errors.tag ? (
          <span className={panel.error}>{errors.tag}</span>
        ) : (
          <span className={panel.help}>{tag ? tagHelp(tag, name, term) : 'None, Academic year or Term.'}</span>
        )}
      </div>

      <div className={panel.fieldRow}>
        <label className={`${panel.field} ${panel.fill}`}>
          <span className={panel.label}>Used by</span>
          <select className={panel.select} value={scope} onChange={(e) => setScope(e.target.value as TypeScope)}>
            <option value="All">Academic and non-academic</option>
            <option value="Academic">Academic units only</option>
            <option value="NonAcademic">Non-academic departments only</option>
          </select>
        </label>
        <label className={`${panel.field} ${panel.fill}`}>
          <span className={panel.label}>Retention</span>
          <select className={panel.select} value={effectiveRetention} onChange={(e) => setRetentionId(e.target.value)}>
            <option value="">No retention rule</option>
            {policies.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
        </label>
      </div>

      <label className={panel.field}>
        <span className={panel.label}>Number pattern</span>
        <input
          className={`${panel.input} ${showError('pattern', 'numberPattern') ? panel.inputInvalid : ''}`}
          value={effectivePattern}
          maxLength={60}
          placeholder="Leave empty for no document number"
          onChange={(e) => {
            setPatternTouched(true)
            setPattern(e.target.value)
          }}
        />
        {showError('pattern', 'numberPattern') ? (
          <span className={panel.error}>{showError('pattern', 'numberPattern')}</span>
        ) : (
          <span className={panel.help}>{patternHelp(effectivePattern)}</span>
        )}
      </label>
      <InlineError error={formError(create.error)} />
    </Modal>
  )
}

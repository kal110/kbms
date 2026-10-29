import { useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { Briefcase, Building, Building2, GraduationCap, Plus, Search, Trash2 } from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import { Link } from 'react-router'
import { ApiError } from '../../api/client'
import {
  createCollege,
  createDepartment,
  createProgram,
  deleteCollege,
  deleteDepartment,
  deleteProgram,
  getOrgStructure,
  updateCollege,
  updateDepartment,
  updateProgram,
} from '../../api/org'
import type { OrgStructureDto, OrgUnitAdminDto, OrgUnitKind } from '../../api/types'
import { useAction, useApi } from '../../api/useApi'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Button } from '../../components/Button'
import { Modal } from '../../components/Modal'
import { Segmented } from '../../components/Segmented'
import { SettingsFrame } from './SettingsFrame'
import panel from './panel.module.css'
import styles from './OrgStructurePage.module.css'

// Settings → Colleges & departments: the units folders, required-documents lists, reviews and
// dashboards hang off. College (Dean) → academic department → program (Academic Supervisor), and
// non-academic departments with a Head. Adding, renaming or moving a unit does the same to its
// folders; a unit can be deleted only while it holds nothing.

const kindLabel: Record<OrgUnitKind, string> = {
  College: 'College',
  Department: 'Academic department',
  Office: 'Non-academic department',
  Program: 'Program',
}

const kindIcon: Record<OrgUnitKind, LucideIcon> = {
  College: Building2,
  Department: Building,
  Office: Briefcase,
  Program: GraduationCap,
}

/** Who heads the unit; academic departments have no head of their own. */
const holderRole: Record<OrgUnitKind, string | null> = {
  College: 'Dean',
  Department: null,
  Office: 'Head',
  Program: 'Academic Supervisor',
}

const kindOptions: { value: OrgUnitKind; label: string }[] = [
  { value: 'College', label: 'College' },
  { value: 'Department', label: 'Department' },
  { value: 'Program', label: 'Program' },
  { value: 'Office', label: 'Non-academic' },
]

const kindHelp: Record<OrgUnitKind, string> = {
  College: 'Led by a Dean. Its academic departments, programs and faculty sit under it.',
  Department: 'An academic department of a college. It has no head; its programs have Academic Supervisors.',
  Program: 'A degree program of an academic department, with its faculty, courses and Academic Supervisor.',
  Office: 'An office outside the colleges, like CICT or the Registrar, with a Head and staff.',
}

const plural = (n: number, one: string, many = `${one}s`) => `${n.toLocaleString('en-US')} ${n === 1 ? one : many}`

/** "a", "a and b", "a, b and c" */
const joinAnd = (parts: string[]) => (parts.length < 2 ? parts.join('') : `${parts.slice(0, -1).join(', ')} and ${parts.at(-1)}`)

const fieldError = (error: unknown, field: string) =>
  error instanceof ApiError ? error.errors?.[field]?.join(' ') : undefined

const formError = (error: unknown) =>
  error instanceof ApiError && error.errors && Object.keys(error.errors).length ? undefined : error

/** What a unit holds, in a few words: "6 faculty · 11 courses · 18 submissions". */
function holds(unit: OrgUnitAdminDto) {
  const u = unit.usage
  const parts = [
    u.units ? plural(u.units, unit.kind === 'College' ? 'department' : 'program') : '',
    u.people ? (unit.kind === 'Office' ? plural(u.people, 'staff member') : `${u.people.toLocaleString('en-US')} faculty`) : '',
    u.courses ? plural(u.courses, 'course') : '',
    u.submissions ? plural(u.submissions, 'submission') : '',
    u.documents ? plural(u.documents, 'document') : '',
  ]
  return parts.filter(Boolean).join(' · ')
}

interface Row {
  unit: OrgUnitAdminDto
  depth: number
}

function rowsOf(structure: OrgStructureDto) {
  const academic: Row[] = structure.colleges.flatMap((c) => [
    { unit: c, depth: 0 },
    ...c.children.flatMap((d) => [{ unit: d, depth: 1 }, ...d.children.map((p) => ({ unit: p, depth: 2 }))]),
  ])
  const offices: Row[] = structure.offices.map((o) => ({ unit: o, depth: 0 }))
  return { academic, offices }
}

// ---- page

export function OrgStructurePage() {
  const structure = useApi((signal) => getOrgStructure(signal), [])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [adding, setAdding] = useState<{ kind: OrgUnitKind; parentId?: string } | null>(null)

  const rows = useMemo(() => (structure.data ? rowsOf(structure.data) : { academic: [], offices: [] }), [structure.data])
  const all = useMemo(() => [...rows.academic, ...rows.offices], [rows])
  const selected = all.find((r) => r.unit.id === selectedId)?.unit ?? all[0]?.unit

  // Searching keeps a match's college and department in view, so it still reads as a tree.
  const q = query.trim().toLowerCase()
  const visibleIds = useMemo(() => {
    if (!q) return null
    const parentOf = new Map(all.map((r) => [r.unit.id, r.unit.parentId]))
    const ids = new Set<string>()
    for (const { unit } of all) {
      if (!`${unit.code} ${unit.name} ${unit.shortName ?? ''}`.toLowerCase().includes(q)) continue
      for (let id: string | null | undefined = unit.id; id; id = parentOf.get(id)) ids.add(id)
    }
    return ids
  }, [all, q])
  const shown = (list: Row[]) => (visibleIds ? list.filter((r) => visibleIds.has(r.unit.id)) : list)

  const count = (kind: OrgUnitKind) => all.filter((r) => r.unit.kind === kind).length

  if (!structure.data) {
    return (
      <SettingsFrame>
        {structure.error ? <ErrorState error={structure.error} onRetry={structure.reload} /> : <Loading label="Loading colleges and departments…" />}
      </SettingsFrame>
    )
  }

  const academic = shown(rows.academic)
  const offices = shown(rows.offices)

  return (
    <SettingsFrame
      actions={
        <Button variant="primary" icon={Plus} onClick={() => setAdding({ kind: 'College' })}>
          Add unit
        </Button>
      }
    >
      <div className={panel.body}>
        <section className={`${panel.card} ${panel.grow}`} aria-label="Colleges and departments">
          <div className={styles.toolbar}>
            <label className={styles.search}>
              <Search size={14} aria-hidden />
              <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Search units…" aria-label="Search units" />
            </label>
            <span className={styles.counter}>
              {plural(count('College'), 'college')} · {plural(count('Department'), 'department')} · {plural(count('Program'), 'program')} ·{' '}
              {count('Office')} non-academic
            </span>
          </div>

          <div className={`${panel.headRow} ${styles.head}`}>
            <span className={`${panel.th} ${panel.fill}`}>Unit</span>
            <span className={`${panel.th} ${styles.colHead}`}>Headed by</span>
            <span className={`${panel.th} ${styles.colHolds}`}>Holds</span>
          </div>

          <div className={`${styles.rows} ${structure.loading ? styles.stale : ''}`}>
            <div className={styles.groupLabel}>Colleges</div>
            {academic.map((r) => (
              <UnitRow key={r.unit.id} row={r} selected={r.unit.id === selected?.id} onSelect={setSelectedId} />
            ))}
            {academic.length === 0 && <div className={panel.empty}>{q ? 'No college, department or program matches.' : 'No colleges yet.'}</div>}
            <div className={styles.groupLabel}>Non-academic departments</div>
            {offices.map((r) => (
              <UnitRow key={r.unit.id} row={r} selected={r.unit.id === selected?.id} onSelect={setSelectedId} />
            ))}
            {offices.length === 0 && <div className={panel.empty}>{q ? 'No non-academic department matches.' : 'None yet.'}</div>}
          </div>

          <div className={styles.footer}>
            Renaming or moving a unit renames or moves its folders too. Deans, Academic Supervisors, Heads, faculty and staff are
            assigned in <Link to="/settings/users">Users & roles</Link>.
          </div>
        </section>

        {selected && (
          <UnitDrawer
            key={selected.id}
            unit={selected}
            structure={structure.data}
            onSaved={structure.reload}
            onDeleted={() => {
              setSelectedId(null)
              structure.reload()
            }}
            onAdd={(kind) => setAdding({ kind, parentId: selected.id })}
          />
        )}
      </div>

      {adding && (
        <NewUnitModal
          initial={adding}
          structure={structure.data}
          onCreated={(unit) => {
            setAdding(null)
            setSelectedId(unit.id)
            setQuery('')
            structure.reload()
          }}
          onClose={() => setAdding(null)}
        />
      )}
    </SettingsFrame>
  )
}

function UnitRow({ row, selected, onSelect }: { row: Row; selected: boolean; onSelect: (id: string) => void }) {
  const { unit, depth } = row
  const Icon = kindIcon[unit.kind]
  const role = holderRole[unit.kind]
  return (
    <button
      type="button"
      className={`${styles.row} ${selected ? styles.selected : ''}`}
      style={{ paddingLeft: 16 + depth * 24 }}
      aria-pressed={selected}
      onClick={() => onSelect(unit.id)}
    >
      <span className={`${styles.unit} ${panel.fill}`}>
        <Icon size={15} className={styles[`icon${unit.kind}`]} aria-hidden />
        <span className={styles.unitCode}>{unit.code}</span>
        <span className={styles.unitName} title={unit.name}>
          {unit.name}
        </span>
      </span>
      <span className={`${panel.cell} ${styles.colHead}`}>
        {unit.holder ? (
          <span className={styles.holder}>
            {unit.holder.displayName}
            <span className={styles.subtle}>{unit.holderTitle ?? role}</span>
          </span>
        ) : role ? (
          <span className={panel.muted}>No {role}</span>
        ) : (
          <span className={panel.muted}>—</span>
        )}
      </span>
      <span className={`${panel.cell} ${styles.colHolds} ${holds(unit) ? '' : panel.muted}`}>{holds(unit) || 'Nothing yet'}</span>
    </button>
  )
}

// ---- edit drawer

interface UnitDrawerProps {
  unit: OrgUnitAdminDto
  structure: OrgStructureDto
  onSaved: () => void
  onDeleted: () => void
  onAdd: (kind: OrgUnitKind) => void
}

function ParentSelect({ kind, structure, value, invalid, onChange }: {
  kind: OrgUnitKind
  structure: OrgStructureDto
  value: string
  invalid?: boolean
  onChange: (id: string) => void
}) {
  if (kind === 'Department') {
    return (
      <select className={`${panel.select} ${invalid ? panel.inputInvalid : ''}`} value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">Pick a college</option>
        {structure.colleges.map((c) => (
          <option key={c.id} value={c.id}>
            {c.code} · {c.name}
          </option>
        ))}
      </select>
    )
  }
  return (
    <select className={`${panel.select} ${invalid ? panel.inputInvalid : ''}`} value={value} onChange={(e) => onChange(e.target.value)}>
      <option value="">Pick a department</option>
      {structure.colleges.map((c) => (
        <optgroup key={c.id} label={`${c.code} · ${c.name}`}>
          {c.children.map((d) => (
            <option key={d.id} value={d.id}>
              {d.code} · {d.name}
            </option>
          ))}
        </optgroup>
      ))}
    </select>
  )
}

function UnitDrawer({ unit, structure, onSaved, onDeleted, onAdd }: UnitDrawerProps) {
  const [code, setCode] = useState(unit.code)
  const [name, setName] = useState(unit.name)
  const [shortName, setShortName] = useState(unit.shortName ?? '')
  const [parentId, setParentId] = useState(unit.parentId ?? '')
  const [saved, setSaved] = useState(false)
  const [deleting, setDeleting] = useState(false)

  const hasShortName = unit.kind === 'Department' || unit.kind === 'Office'
  const movable = unit.kind === 'Department' || unit.kind === 'Program'
  const changes = {
    code: code.trim() !== unit.code ? code.trim() : null,
    name: name.trim() !== unit.name ? name.trim() : null,
    shortName: hasShortName && shortName.trim() !== (unit.shortName ?? '') ? shortName.trim() : null,
    parentId: movable && parentId && parentId !== unit.parentId ? parentId : null,
  }
  const dirty = Object.values(changes).some((v) => v !== null)
  const missing = !code.trim() || !name.trim()

  const save = useAction(() => {
    switch (unit.kind) {
      case 'College':
        return updateCollege(unit.id, { code: changes.code, name: changes.name })
      case 'Program':
        return updateProgram(unit.id, { code: changes.code, name: changes.name, departmentId: changes.parentId })
      default:
        return updateDepartment(unit.id, { code: changes.code, name: changes.name, shortName: changes.shortName, collegeId: changes.parentId })
    }
  })

  const submit = async () => {
    if (!dirty || missing) return
    const next = await save.run()
    if (!next) return
    setCode(next.code)
    setName(next.name)
    setShortName(next.shortName ?? '')
    setParentId(next.parentId ?? '')
    setSaved(true)
    onSaved()
  }

  const role = holderRole[unit.kind]
  const blockers = unit.usage.blockers
  const parentName = (() => {
    for (const c of structure.colleges) {
      if (c.id === unit.parentId) return c.name
      const d = c.children.find((x) => x.id === unit.parentId)
      if (d) return `${d.name}, ${c.code}`
    }
    return null
  })()

  return (
    <aside className={panel.drawer} aria-label={`Edit ${kindLabel[unit.kind].toLowerCase()}`}>
      <div className={panel.drawerHead}>
        <span className={panel.eyebrow}>{kindLabel[unit.kind]}</span>
        <span className={panel.drawerTitle}>{unit.name}</span>
        <span className={panel.drawerSub}>{[unit.code, parentName].filter(Boolean).join(' · ')}</span>
      </div>
      <div className={panel.rule} />

      <div className={`${panel.form} ${styles.drawerScroll}`}>
        <div className={panel.fieldRow}>
          <label className={`${panel.field} ${panel.fill}`}>
            <span className={panel.label}>Name</span>
            <input
              className={`${panel.input} ${!name.trim() || fieldError(save.error, 'name') ? panel.inputInvalid : ''}`}
              value={name}
              maxLength={200}
              onChange={(e) => {
                setSaved(false)
                setName(e.target.value)
              }}
            />
          </label>
          <label className={panel.field} style={{ width: 110, flexShrink: 0 }}>
            <span className={panel.label}>Code</span>
            <input
              className={`${panel.input} ${!code.trim() || fieldError(save.error, 'code') ? panel.inputInvalid : ''}`}
              value={code}
              maxLength={20}
              onChange={(e) => {
                setSaved(false)
                setCode(e.target.value)
              }}
            />
          </label>
        </div>
        {(fieldError(save.error, 'name') || fieldError(save.error, 'code')) && (
          <span className={panel.error}>{fieldError(save.error, 'name') ?? fieldError(save.error, 'code')}</span>
        )}
        {changes.code && (
          <span className={panel.help}>Teaching-load files name programs by code: use the new code in the next import.</span>
        )}

        {hasShortName && (
          <label className={panel.field}>
            <span className={panel.label}>Short name (optional)</span>
            <input
              className={`${panel.input} ${fieldError(save.error, 'shortName') ? panel.inputInvalid : ''}`}
              value={shortName}
              maxLength={120}
              placeholder={unit.kind === 'Department' ? 'e.g. Computer Science' : 'e.g. Registrar'}
              onChange={(e) => {
                setSaved(false)
                setShortName(e.target.value)
              }}
            />
            {fieldError(save.error, 'shortName') ? (
              <span className={panel.error}>{fieldError(save.error, 'shortName')}</span>
            ) : (
              <span className={panel.help}>
                {unit.kind === 'Department' ? 'Its folder is named this, when set.' : 'A shorter name for lists.'}
              </span>
            )}
          </label>
        )}

        {movable && (
          <label className={panel.field}>
            <span className={panel.label}>{unit.kind === 'Department' ? 'College' : 'Department'}</span>
            <ParentSelect
              kind={unit.kind}
              structure={structure}
              value={parentId}
              invalid={Boolean(fieldError(save.error, unit.kind === 'Department' ? 'collegeId' : 'departmentId'))}
              onChange={(id) => {
                setSaved(false)
                setParentId(id)
              }}
            />
            {fieldError(save.error, unit.kind === 'Department' ? 'collegeId' : 'departmentId') ? (
              <span className={panel.error}>{fieldError(save.error, unit.kind === 'Department' ? 'collegeId' : 'departmentId')}</span>
            ) : changes.parentId ? (
              <span className={panel.warn}>
                {unit.kind === 'Program'
                  ? 'Its faculty, courses, submissions and folders move with it. New submissions follow the new department’s and college’s required-documents lists.'
                  : 'Its programs and folders move with it, and the new college’s Dean takes over its reviews and folders.'}
              </span>
            ) : null}
          </label>
        )}

        <div className={panel.box}>
          <span className={panel.boxLabel}>Holds</span>
          <span className={panel.boxLine}>{holds(unit) || 'Nothing yet.'}</span>
          {unit.usage.requirementLists > 0 && (
            <span className={panel.boxLine}>{plural(unit.usage.requirementLists, 'required-documents list')} (all versions)</span>
          )}
          {role && (
            <span className={panel.boxLine}>
              {unit.holder ? `${unit.holderTitle ?? role}: ${unit.holder.displayName}` : `No ${role} yet`} ·{' '}
              <Link to="/settings/users">{unit.holder ? 'change' : 'assign'} in Users & roles</Link>
            </span>
          )}
        </div>

        {(unit.kind === 'College' || unit.kind === 'Department') && (
          <Button size="sm" icon={Plus} className={styles.addChild} onClick={() => onAdd(unit.kind === 'College' ? 'Department' : 'Program')}>
            {unit.kind === 'College' ? `Add a department to ${unit.code}` : `Add a program to ${unit.code}`}
          </Button>
        )}

        {blockers.length > 0 && (
          <div className={styles.blocked}>
            <span className={styles.blockedTitle}>Can’t be deleted yet</span>
            <span>
              {unit.code} still has {joinAnd(blockers)}.{' '}
              {unit.usage.submissions || unit.usage.documents
                ? 'Units with submissions or documents stay, so their records keep their place.'
                : 'Move or remove them first.'}
            </span>
          </div>
        )}
      </div>

      {formError(save.error) ? (
        <div className={styles.actionError}>
          <InlineError error={formError(save.error)} />
        </div>
      ) : null}
      <div className={panel.drawerFooter}>
        <Button
          variant="ghost"
          icon={Trash2}
          className={styles.deleteButton}
          disabled={blockers.length > 0}
          title={blockers.length ? `Still has ${joinAnd(blockers)}` : undefined}
          onClick={() => setDeleting(true)}
        >
          Delete
        </Button>
        <span className={styles.footerActions}>
          {saved && !dirty && <span className={styles.saved}>Saved</span>}
          <Button
            disabled={!dirty || save.pending}
            onClick={() => {
              setCode(unit.code)
              setName(unit.name)
              setShortName(unit.shortName ?? '')
              setParentId(unit.parentId ?? '')
            }}
          >
            Discard
          </Button>
          <Button variant="primary" disabled={!dirty || missing || save.pending} onClick={() => void submit()}>
            {save.pending ? 'Saving…' : 'Save changes'}
          </Button>
        </span>
      </div>

      {deleting && <DeleteUnitModal unit={unit} onDeleted={onDeleted} onClose={() => setDeleting(false)} />}
    </aside>
  )
}

function DeleteUnitModal({ unit, onDeleted, onClose }: { unit: OrgUnitAdminDto; onDeleted: () => void; onClose: () => void }) {
  // Deletes answer 204, so success is said explicitly: run() gives undefined on failure.
  const remove = useAction(async () => {
    await (unit.kind === 'College' ? deleteCollege(unit.id) : unit.kind === 'Program' ? deleteProgram(unit.id) : deleteDepartment(unit.id))
    return true
  })
  const role = holderRole[unit.kind]
  const kind = kindLabel[unit.kind].toLowerCase()

  const effects: ReactNode[] = [
    'Its folder in the library is deleted; no document is filed in it.',
    unit.usage.requirementLists > 0 &&
      `Its ${plural(unit.usage.requirementLists, 'required-documents list')} ${unit.usage.requirementLists === 1 ? 'is' : 'are'} deleted; no submission was checked against ${unit.usage.requirementLists === 1 ? 'it' : 'them'}.`,
    unit.holder && role && `${unit.holder.displayName} stops being its ${unit.holderTitle ?? role}.`,
    'Access given to it on other folders is removed.',
  ].filter(Boolean)

  return (
    <Modal
      title={`Delete ${unit.code}?`}
      description={`${unit.name} · ${kind}`}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="danger"
            disabled={remove.pending}
            onClick={async () => {
              if (await remove.run()) onDeleted()
            }}
          >
            {remove.pending ? 'Deleting…' : `Delete ${kind}`}
          </Button>
        </>
      }
    >
      <ul className={styles.effects}>
        {effects.map((e, i) => (
          <li key={i}>{e}</li>
        ))}
      </ul>
      <span className={panel.help}>This can’t be undone; add it again if you need it back.</span>
      <InlineError error={remove.error} />
    </Modal>
  )
}

// ---- add

interface NewUnitModalProps {
  initial: { kind: OrgUnitKind; parentId?: string }
  structure: OrgStructureDto
  onCreated: (unit: OrgUnitAdminDto) => void
  onClose: () => void
}

function NewUnitModal({ initial, structure, onCreated, onClose }: NewUnitModalProps) {
  const [kind, setKind] = useState<OrgUnitKind>(initial.kind)
  const [code, setCode] = useState('')
  const [name, setName] = useState('')
  const [shortName, setShortName] = useState('')
  const [parentId, setParentId] = useState(initial.parentId ?? '')
  const [submitted, setSubmitted] = useState(false)

  const create = useAction(() => {
    switch (kind) {
      case 'College':
        return createCollege({ code: code.trim(), name: name.trim() })
      case 'Program':
        return createProgram({ code: code.trim(), name: name.trim(), departmentId: parentId })
      default:
        return createDepartment({
          code: code.trim(),
          name: name.trim(),
          shortName: shortName.trim() || null,
          kind: kind === 'Department' ? 'Academic' : 'NonAcademic',
          collegeId: kind === 'Department' ? parentId : null,
        })
    }
  })

  const needsParent = kind === 'Department' || kind === 'Program'
  const parentField = kind === 'Department' ? 'collegeId' : 'departmentId'
  const errors = {
    name: !name.trim() ? 'Give it a name.' : null,
    code: !code.trim() ? 'Give it a code.' : null,
    parent: needsParent && !parentId ? (kind === 'Department' ? 'Pick its college.' : 'Pick its department.') : null,
  }
  const valid = !errors.name && !errors.code && !errors.parent
  const show = (key: keyof typeof errors, field: string) => (submitted && errors[key]) || fieldError(create.error, field) || null

  // A parent picked for one kind rarely fits another: a college isn't a program's department.
  const switchKind = (next: OrgUnitKind) => {
    setKind(next)
    const colleges = new Set(structure.colleges.map((c) => c.id))
    const departments = new Set(structure.colleges.flatMap((c) => c.children.map((d) => d.id)))
    if ((next === 'Department' && !colleges.has(parentId)) || (next === 'Program' && !departments.has(parentId))) setParentId('')
  }

  const submit = async () => {
    setSubmitted(true)
    if (!valid) return
    const unit = await create.run()
    if (unit) onCreated(unit)
  }

  return (
    <Modal
      title="Add a unit"
      description="Its folder appears in the library right away; assign its head and people in Users & roles."
      width={560}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" disabled={(submitted && !valid) || create.pending} onClick={() => void submit()}>
            {create.pending ? 'Adding…' : `Add ${kindLabel[kind].toLowerCase()}`}
          </Button>
        </>
      }
    >
      <div className={panel.field}>
        <span className={panel.label}>Kind</span>
        <Segmented options={kindOptions} value={kind} onChange={switchKind} ariaLabel="Kind of unit" />
        <span className={panel.help}>{kindHelp[kind]}</span>
      </div>

      {needsParent && (
        <label className={panel.field}>
          <span className={panel.label}>{kind === 'Department' ? 'College' : 'Department'}</span>
          <ParentSelect kind={kind} structure={structure} value={parentId} invalid={Boolean(show('parent', parentField))} onChange={setParentId} />
          {show('parent', parentField) && <span className={panel.error}>{show('parent', parentField)}</span>}
        </label>
      )}

      <div className={panel.fieldRow}>
        <label className={`${panel.field} ${panel.fill}`}>
          <span className={panel.label}>Name</span>
          <input
            className={`${panel.input} ${show('name', 'name') ? panel.inputInvalid : ''}`}
            value={name}
            autoFocus
            maxLength={200}
            placeholder={
              { College: 'e.g. College of Education', Department: 'e.g. Department of Mathematics', Program: 'e.g. BS Mathematics', Office: 'e.g. Library' }[kind]
            }
            onChange={(e) => setName(e.target.value)}
          />
          {show('name', 'name') && <span className={panel.error}>{show('name', 'name')}</span>}
        </label>
        <label className={panel.field} style={{ width: 130, flexShrink: 0 }}>
          <span className={panel.label}>Code</span>
          <input
            className={`${panel.input} ${show('code', 'code') ? panel.inputInvalid : ''}`}
            value={code}
            maxLength={20}
            placeholder={{ College: 'CED', Department: 'DMATH', Program: 'BSMATH', Office: 'LIB' }[kind]}
            onChange={(e) => setCode(e.target.value)}
          />
          {show('code', 'code') && <span className={panel.error}>{show('code', 'code')}</span>}
        </label>
      </div>

      {(kind === 'Department' || kind === 'Office') && (
        <label className={panel.field}>
          <span className={panel.label}>Short name (optional)</span>
          <input
            className={`${panel.input} ${fieldError(create.error, 'shortName') ? panel.inputInvalid : ''}`}
            value={shortName}
            maxLength={120}
            placeholder={kind === 'Department' ? 'e.g. Mathematics' : 'e.g. Library'}
            onChange={(e) => setShortName(e.target.value)}
          />
          <span className={panel.help}>{kind === 'Department' ? 'Its folder is named this, when set.' : 'A shorter name for lists.'}</span>
        </label>
      )}
      <InlineError error={formError(create.error)} />
    </Modal>
  )
}

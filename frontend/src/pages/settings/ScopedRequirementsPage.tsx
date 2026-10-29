import { useState } from 'react'
import type { ComponentType, ReactNode } from 'react'
import {
  ArrowRight,
  Building,
  Building2,
  ChevronDown,
  ChevronRight,
  Eye,
  GraduationCap,
  GripVertical,
  Info,
  Lock,
  Plus,
  Route,
  Users,
  X,
} from 'lucide-react'
import { getDepartment, getOrgTree, getProgram } from '../../api/org'
import { getAcademicYears, getDocumentTypes, termLabel } from '../../api/reference'
import { getCopySources, getRequirementScopeTree, getScopeRequirementSets } from '../../api/requirements'
import type {
  AcademicTermDto,
  DocumentTypeDto,
  FileLimit,
  OrgCollegeDto,
  OrgDepartmentDto,
  OrgTreeDto,
  RequirementItemDto,
  RequirementItemInput,
  RequirementRule,
  RequirementScopeNodeDto,
  RequirementScopeTreeDto,
  SubmissionTypeRefDto,
} from '../../api/types'
import { useApi } from '../../api/useApi'
import { can } from '../../app/navigation'
import { useMe } from '../../auth/SessionContext'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { TagChip, TypeChip } from '../../components/Chips'
import { Modal } from '../../components/Modal'
import { Segmented } from '../../components/Segmented'
import {
  errorsByKey,
  filesFor,
  filesLabel,
  keyFromLabel,
  ruleOptions,
  slotFieldErrors,
  toInput,
  uniqueKey,
} from '../requirements/requirementsUi'
import { useRequirementDraft } from '../requirements/useRequirementDraft'
import { SettingsFrame } from './SettingsFrame'
import panel from './panel.module.css'
import styles from './ScopedRequirementsPage.module.css'

type ScopeKind = 'College' | 'AcademicDept' | 'NonAcademic'

/** The header and tabs around the editor: Settings for the admin, Required documents for the VPAF. */
type PageFrame = ComponentType<{ actions?: ReactNode; children: ReactNode }>

/** A college or department the admin sets a list for, with its place in the org tree. */
interface Scope {
  node: RequirementScopeNodeDto
  kind: ScopeKind
  name: string
  /** The college above an academic department (or the college itself). */
  college?: OrgCollegeDto
  department?: OrgDepartmentDto
}

/** More submission types than this go in a "More types" picker instead of the segmented control. */
const segmentLimit = 6

function scopesOf(tree: RequirementScopeTreeDto, org: OrgTreeDto): Scope[] {
  const departments = [...org.colleges.flatMap((c) => c.departments), ...org.nonAcademicDepartments]
  const toScope = (node: RequirementScopeNodeDto): Scope | undefined => {
    if (node.scopeType === 'College') {
      return { node, kind: 'College', name: node.name, college: org.colleges.find((c) => c.id === node.id) }
    }
    if (node.scopeType !== 'Department') return undefined // Programs: Required documents → Programs (frame 18).
    const department = departments.find((d) => d.id === node.id)
    if (node.kind === 'NonAcademic') return { node, kind: 'NonAcademic', name: node.name, department }
    return {
      node,
      kind: 'AcademicDept',
      name: department?.shortName ?? node.name,
      department,
      college: org.colleges.find((c) => c.id === department?.collegeId),
    }
  }
  return [...tree.academic, ...tree.academic.flatMap((n) => n.children), ...tree.nonAcademic]
    .map(toScope)
    .filter((s): s is Scope => Boolean(s))
}

const countLabel = (node: RequirementScopeNodeDto) => {
  if (node.publishedCount) return `${node.publishedCount} list${node.publishedCount === 1 ? '' : 's'}`
  if (node.draftCount) return `${node.draftCount} draft${node.draftCount === 1 ? '' : 's'}`
  return '—'
}

const programsUnder = (scope: Scope) =>
  scope.kind === 'College' ? (scope.college?.departments.flatMap((d) => d.programs) ?? []) : (scope.department?.programs ?? [])

/** Choices for a slot's file count; the current one is kept even when it isn't a usual choice. */
function fileChoices(rule: RequirementRule, current?: FileLimit): FileLimit[] {
  const min = rule === 'Required' ? 1 : 0
  const choices = [1, 2, 3, 5].map((max) => ({ min, max }))
  if (current && !choices.some((f) => f.min === current.min && f.max === current.max)) choices.push(current)
  return choices
}

const fileValue = (f: FileLimit) => `${f.min}-${f.max}`
const parseFiles = (value: string): FileLimit => {
  const [min, max] = value.split('-').map(Number)
  return { min, max }
}

/** Where a submitted document is filed: the submitter's own folder, then year / term from the type's tag. */
function folderPath(base: string[], tag: DocumentTypeDto['tag'] | undefined, term: AcademicTermDto | undefined) {
  if (!term || !tag || tag === 'None') return base
  return tag === 'Year' ? [...base, term.academicYearName] : [...base, term.academicYearName, term.name]
}

export function ScopedRequirementsPage({ frame: Frame = SettingsFrame }: { frame?: PageFrame }) {
  const tree = useApi((signal) => getRequirementScopeTree(signal), [])
  const reference = useApi(() => Promise.all([getDocumentTypes(true), getAcademicYears(), getOrgTree()]), [])

  if (!tree.data || !reference.data) {
    const error = tree.error ?? reference.error
    return (
      <Frame>
        <div className={panel.body}>
          {error ? (
            <ErrorState
              error={error}
              onRetry={() => {
                tree.reload()
                reference.reload()
              }}
            />
          ) : (
            <Loading label="Loading required documents…" />
          )}
        </div>
      </Frame>
    )
  }

  const [documentTypes, years, org] = reference.data
  return (
    <ScopedEditor
      frame={Frame}
      scopes={scopesOf(tree.data, org)}
      tree={tree.data}
      reloadTree={tree.reload}
      documentTypes={documentTypes}
      activeTerm={years.flatMap((y) => y.terms).find((t) => t.status === 'Active')}
    />
  )
}

interface ScopedEditorProps {
  frame: PageFrame
  scopes: Scope[]
  tree: RequirementScopeTreeDto
  reloadTree: () => void
  documentTypes: DocumentTypeDto[]
  activeTerm?: AcademicTermDto
}

function ScopedEditor({ frame: Frame, scopes, tree, reloadTree, documentTypes, activeTerm }: ScopedEditorProps) {
  const me = useMe()
  const [scopeId, setScopeId] = useState<string | null>(null)
  const [typeByScope, setTypeByScope] = useState<Record<string, string>>({})
  // Open by default: the first college and its first department; clicks toggle from there.
  const [toggled, setToggled] = useState<Set<string>>(() => new Set())
  const [modal, setModal] = useState<'add' | 'copy' | 'preview' | null>(null)
  const [justPublished, setJustPublished] = useState<string | null>(null)
  const [dragKey, setDragKey] = useState<string | null>(null)
  const [overKey, setOverKey] = useState<string | null>(null)

  const scope =
    scopes.find((s) => s.node.id === scopeId) ??
    scopes.find((s) => s.node.draftCount > 0) ??
    scopes.find((s) => s.node.publishedCount > 0) ??
    scopes[0]
  const scopeType = scope?.node.scopeType ?? 'College'
  const sets = useApi(
    (signal) => (scope ? getScopeRequirementSets(scope.node.scopeType, scope.node.id, signal) : Promise.resolve(undefined)),
    [scope?.node.scopeType, scope?.node.id],
  )
  const draftOps = useRequirementDraft(scopeType, scope?.node.id ?? '', sets.setData, sets.reload)

  const firstCollege = tree.academic.find((n) => n.scopeType === 'College')
  const defaultOpen = new Set([firstCollege?.id, firstCollege?.children[0]?.id].filter(Boolean))
  const isOpen = (id: string) => defaultOpen.has(id) !== toggled.has(id)
  const toggle = (id: string) =>
    setToggled((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })

  const selectScope = (id: string) => {
    setScopeId(id)
    setJustPublished(null)
    draftOps.clearError()
  }

  // Only the selected scope's lists, not the previous scope's while the next one loads.
  const data = sets.data && scope && sets.data.scope.id === scope.node.id ? sets.data : undefined
  const editable = Boolean(data?.canManage) && can(me, 'canSetScopedRequirements')
  const busy = draftOps.pending || !editable

  const types: SubmissionTypeRefDto[] = data
    ? [
        ...data.availableTypes,
        ...data.sets.map((g) => g.submissionType).filter((t) => !data.availableTypes.some((a) => a.id === t.id)),
      ]
    : []
  const groupOf = (typeId: string) => data?.sets.find((g) => g.submissionType.id === typeId)
  const type =
    types.find((t) => t.id === typeByScope[scope?.node.id ?? '']) ??
    types.find((t) => groupOf(t.id)?.draft) ??
    types.find((t) => groupOf(t.id)?.published) ??
    types.find((t) => groupOf(t.id)?.inherited.length) ??
    types[0]

  const group = type ? groupOf(type.id) : undefined
  const published = group?.published ?? null
  const draft = group?.draft ?? null
  const items = draft?.items ?? published?.items ?? []
  const inherited = group?.inherited ?? []
  const publishedKeys = new Set(published?.items.map((i) => i.key))
  const draftVersion = draft?.version ?? (published?.version ?? 0) + 1
  const rowErrors = modal ? {} : errorsByKey(draftOps.error, draftOps.sentKeys)

  // A document type is required once per checklist: not again here, nor below a list that already has it.
  const blocked = new Map<string, string>()
  inherited.forEach((r) => blocked.set(r.documentTypeId, `already required by ${r.source?.code ?? 'a broader list'}`))
  items.forEach((r) => blocked.set(r.documentTypeId, 'already in this list'))

  const editItems = (fn: (list: RequirementItemInput[]) => RequirementItemInput[]) => {
    setJustPublished(null)
    return type ? draftOps.edit(group, type.id, (req) => ({ ...req, items: fn(req.items) })) : Promise.resolve(undefined)
  }

  const moveItem = (from: string, to: string) => {
    if (from === to) return
    editItems((current) => {
      const fromIndex = current.findIndex((r) => r.key === from)
      const toIndex = current.findIndex((r) => r.key === to)
      if (fromIndex < 0 || toIndex < 0) return current
      const next = [...current]
      const [moved] = next.splice(fromIndex, 1)
      next.splice(toIndex, 0, moved)
      return next
    })
  }

  const publish = async () => {
    if (!draft) return
    const result = await draftOps.publish(draft)
    if (!result) return
    setJustPublished(`v${result.version} published`)
    reloadTree()
  }

  const discard = async () => {
    if (!draft) return
    if ((await draftOps.discard(draft)) !== undefined) reloadTree()
  }

  const closeModal = () => {
    setModal(null)
    draftOps.clearError()
  }

  const frameAction = (
    <Button disabled={!editable || !type || draftOps.pending} onClick={() => setModal('copy')}>
      Copy from another college or department
    </Button>
  )

  if (!scope) {
    return (
      <Frame>
        <div className={panel.body}>
          <p className={panel.help}>There are no colleges or departments to set required documents for.</p>
        </div>
      </Frame>
    )
  }

  const department = scope.department
  const programs = programsUnder(scope)
  const facultyCount = programs.reduce((n, p) => n + p.facultyCount, 0)
  const reviewers = type?.deanStage ? 'AS, then Dean reviews' : 'AS reviews'
  const yearName = activeTerm?.academicYearName ?? '{year}'
  const termName = activeTerm?.name ?? '{term}'

  let headMeta: string
  let audience: string
  let previewLabel: string
  let mapping: string
  let addsUp: string
  if (scope.kind === 'NonAcademic') {
    const title = department?.headTitle ?? 'Head'
    const head = department?.head ? `${department.head.displayName}, ${title}` : 'not assigned'
    const staff = department?.staffCount ?? 0
    headMeta = `Head: ${head} · ${staff} staff`
    audience = `All ${staff} staff · ${department?.head ? title : 'Head'} reviews`
    previewLabel = `Preview as ${scope.name} staff`
    mapping = `Filed in each submitter's own folder: Non-academic departments / ${scope.name} / {staff member} / ${yearName} / ${termName} for Term types, or / ${yearName} for Year types. The ${title} can open the whole ${scope.name} folder.`
    addsUp = `${scope.node.code} staff get only the ${scope.node.code} list, because a non-academic department has no programs.`
  } else if (scope.kind === 'College') {
    const code = scope.node.code
    headMeta = `Dean: ${scope.college?.dean?.displayName ?? 'not assigned'} · ${scope.college?.departments.length ?? 0} departments · ${programs.length} programs`
    audience = `All ${facultyCount} faculty in ${code} · ${reviewers}`
    previewLabel = `Preview as ${code} faculty`
    mapping = `Filed in each submitter's own folder: Colleges / ${scope.name} / {department} / {program} / {faculty member} / ${yearName} / ${termName} for Term types, or / ${yearName} for Year types. The Dean can open the whole ${code} folder.`
    addsUp = programs[0]
      ? `A ${programs[0].code} faculty member starting a ${type?.name ?? 'submission'} gets the ${code} list, plus their department's list and the ${programs[0].code} list.`
      : `Faculty in ${code} get the ${code} list, plus their department's and program's lists.`
  } else {
    const code = scope.college?.code ?? ''
    headMeta = `No department head · ${programs.length} programs, each led by its AS`
    audience = `${facultyCount} faculty in ${programs.map((p) => p.code).join(', ') || 'no programs yet'} · ${reviewers}`
    previewLabel = `Preview as ${scope.name} faculty`
    mapping = `Filed in each submitter's own folder: Colleges / ${scope.college?.name ?? code} / ${scope.name} / {program} / {faculty member} / ${yearName} / ${termName} for Term types, or / ${yearName} for Year types. Academic departments have no head; the Dean can open the whole ${code} folder.`
    addsUp = programs[0]
      ? `A ${programs[0].code} faculty member starting a ${type?.name ?? 'submission'} gets the ${code} list, plus the ${scope.node.code} list and the ${programs[0].code} list.`
      : `Faculty here get the ${code} list, plus the ${scope.node.code} list and their program's list.`
  }

  const kindBadge =
    scope.kind === 'NonAcademic' ? (
      <Badge tone="info">Non-academic department</Badge>
    ) : scope.kind === 'College' ? (
      <Badge tone="purple">College</Badge>
    ) : (
      <Badge tone="neutral">Academic department</Badge>
    )

  // Few types: one segmented control. Many (colleges allow every type): the ones in use, plus a picker.
  const segmentTypes =
    types.length <= segmentLimit
      ? types
      : types.filter((t) => t.id === type?.id || groupOf(t.id)?.published || groupOf(t.id)?.draft || groupOf(t.id)?.inherited.length)
  const moreTypes = types.filter((t) => !segmentTypes.includes(t))
  const pickType = (id: string) => {
    setTypeByScope((m) => ({ ...m, [scope.node.id]: id }))
    setJustPublished(null)
    draftOps.clearError()
  }

  const renderScopeRow = (s: Scope, depth: 0 | 1, opts: { expandable: boolean }) => {
    const id = s.node.id
    const open = isOpen(id)
    const isCollege = s.kind === 'College'
    const Icon = isCollege ? Building2 : Building
    return (
      <div className={`${styles.scope} ${id === scope.node.id ? styles.scopeSelected : ''} ${isCollege ? styles.college : ''}`}>
        {depth === 1 && <span className={styles.indent} />}
        {opts.expandable ? (
          <button
            type="button"
            className={styles.toggle}
            onClick={() => toggle(id)}
            aria-label={open ? `Collapse ${s.name}` : `Expand ${s.name}`}
            aria-expanded={open}
          >
            {open ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
          </button>
        ) : (
          <span className={styles.chevronSpace} />
        )}
        <button type="button" className={styles.scopeButton} onClick={() => selectScope(id)} aria-current={id === scope.node.id}>
          <Icon size={16} className={isCollege ? styles.iconCollege : styles.iconDept} aria-hidden />
          <span className={styles.scopeName}>{s.name}</span>
          {s.kind === 'NonAcademic' && s.department?.head && s.department.headTitle && (
            <span className={styles.headChip}>{s.department.headTitle}</span>
          )}
          <span className={styles.count}>{countLabel(s.node)}</span>
        </button>
      </div>
    )
  }

  const colleges = scopes.filter((s) => s.kind === 'College')
  const departmentsOf = (collegeId: string) =>
    scopes.filter((s) => s.kind === 'AcademicDept' && s.college?.id === collegeId)
  // Academic departments the caller manages without managing their college.
  const looseDepartments = scopes.filter((s) => s.kind === 'AcademicDept' && !colleges.some((c) => c.node.id === s.college?.id))
  const nonAcademic = scopes.filter((s) => s.kind === 'NonAcademic')

  return (
    <Frame actions={frameAction}>
      <div className={panel.body}>
        <nav className={styles.tree} aria-label="College or department">
          <span className={`${panel.eyebrow} ${styles.treeLabel}`}>Set required documents for</span>
          {(colleges.length > 0 || looseDepartments.length > 0) && <span className={styles.section}>Colleges</span>}
          {colleges.map((college) => (
            <div key={college.node.id}>
              {renderScopeRow(college, 0, { expandable: true })}
              {isOpen(college.node.id) &&
                departmentsOf(college.node.id).map((dept) => {
                  const progs = dept.department?.programs ?? []
                  return (
                    <div key={dept.node.id}>
                      {renderScopeRow(dept, 1, { expandable: true })}
                      {isOpen(dept.node.id) && (
                        <div className={styles.programsHint}>
                          <GraduationCap size={14} aria-hidden />
                          {progs.length ? `${progs.map((p) => p.code).join(', ')} · each has its own list` : 'No programs yet'}
                        </div>
                      )}
                    </div>
                  )
                })}
            </div>
          ))}
          {looseDepartments.map((dept) => (
            <div key={dept.node.id}>{renderScopeRow(dept, 0, { expandable: false })}</div>
          ))}
          {nonAcademic.length > 0 && <span className={styles.section}>Non-academic departments</span>}
          {nonAcademic.map((dept) => (
            <div key={dept.node.id}>{renderScopeRow(dept, 0, { expandable: false })}</div>
          ))}
          <div className={styles.hint}>
            <Info size={14} aria-hidden />
            You set lists for colleges and departments here, and each program&apos;s own list in Required documents → Programs.
            Lists add up from the college down to the program.
          </div>
        </nav>

        <section className={styles.editor} aria-label={`Required documents for ${scope.name}`}>
          <div className={styles.editorHead}>
            {scope.kind === 'College' ? (
              <Building2 size={20} className={styles.editorIcon} aria-hidden />
            ) : (
              <Building size={20} className={styles.editorIcon} aria-hidden />
            )}
            <span className={styles.editorTitle}>{scope.name}</span>
            {kindBadge}
            {draft && <Badge tone="warning">Draft v{draftVersion}</Badge>}
            {published && <Badge tone="success">v{published.version} published</Badge>}
            {data && type && !published && !draft && <Badge tone="neutral">No list yet</Badge>}
            {data && !data.canManage && <Badge tone="neutral">View only</Badge>}
            <span className={styles.headMeta}>{headMeta}</span>
          </div>

          {!data ? (
            sets.error ? (
              <ErrorState error={sets.error} onRetry={sets.reload} />
            ) : (
              <Loading label={`Loading ${scope.name} lists…`} />
            )
          ) : !type ? (
            <div className={styles.emptyList}>No submission types can have a list at this level.</div>
          ) : (
            <>
              <div className={styles.typeRow}>
                <span className={styles.typeLabel}>Submission type</span>
                <div className={styles.typeSeg}>
                  <Segmented
                    size="sm"
                    options={segmentTypes.map((t) => ({ value: t.id, label: t.name }))}
                    value={type.id}
                    onChange={pickType}
                    ariaLabel="Submission type"
                  />
                </div>
                {moreTypes.length > 0 && (
                  <select
                    className={styles.moreTypes}
                    aria-label="Other submission types"
                    value=""
                    onChange={(e) => e.target.value && pickType(e.target.value)}
                  >
                    <option value="">More types ({moreTypes.length})…</option>
                    {moreTypes.map((t) => (
                      <option key={t.id} value={t.id}>
                        {t.name}
                      </option>
                    ))}
                  </select>
                )}
                <span className={styles.audience}>
                  <Users size={14} aria-hidden />
                  {audience}
                </span>
              </div>

              <div className={styles.requirements}>
                <div className={`${panel.headRow} ${styles.reqHead}`}>
                  <span className={styles.gripSpace} />
                  <span className={`${panel.th} ${panel.fill}`}>Required document</span>
                  <span className={`${panel.th} ${styles.colType}`}>Document type (tag)</span>
                  <span className={`${panel.th} ${styles.colRule}`}>Rule</span>
                  <span className={`${panel.th} ${styles.colFiles}`}>Files</span>
                  <span className={styles.actionSpace} />
                </div>

                {inherited.map((r) => (
                  <RequirementRow key={`inh-${r.source?.id}-${r.key}`} item={r} documentTypes={documentTypes} inheritedFrom={r.source?.code ?? 'above'} />
                ))}

                {items.map((r) => (
                  <RequirementRow
                    key={r.key}
                    item={r}
                    documentTypes={documentTypes}
                    readOnly={!editable}
                    disabled={busy}
                    error={rowErrors[r.key]}
                    isNew={Boolean(draft) && !publishedKeys.has(r.key)}
                    newLabel={`New in v${draftVersion}`}
                    dragging={dragKey === r.key}
                    dropTarget={overKey === r.key && dragKey !== r.key}
                    onRule={(rule) => editItems((all) => all.map((x) => (x.key === r.key ? { ...x, rule, files: filesFor(rule, x.files) } : x)))}
                    onFiles={(files) => editItems((all) => all.map((x) => (x.key === r.key ? { ...x, files } : x)))}
                    onRemove={() => editItems((all) => all.filter((x) => x.key !== r.key))}
                    onDragStart={() => setDragKey(r.key)}
                    onDragOver={() => setOverKey(r.key)}
                    onDrop={() => {
                      if (dragKey) moveItem(dragKey, r.key)
                      setDragKey(null)
                      setOverKey(null)
                    }}
                    onDragEnd={() => {
                      setDragKey(null)
                      setOverKey(null)
                    }}
                  />
                ))}

                {items.length === 0 && inherited.length === 0 && (
                  <div className={styles.emptyList}>Nothing is required for “{type.name}” at this level yet.</div>
                )}

                {editable && (
                  <button type="button" className={styles.addRow} disabled={busy} onClick={() => setModal('add')}>
                    <span className={styles.addLabel}>
                      <Plus size={15} aria-hidden />
                      Add requirement
                    </span>
                    <span className={styles.addHint}>
                      — pick a document type first; a type already required above this level can&apos;t be added again
                    </span>
                  </button>
                )}
              </div>

              <div className={styles.mapping}>
                <Route size={16} aria-hidden />
                <span>{mapping}</span>
              </div>

              <div className={styles.addsUp}>
                <span className={panel.eyebrow}>How lists add up</span>
                <div className={styles.chain}>
                  <span className={`${styles.chainChip} ${styles.chainCollege} ${scope.kind === 'NonAcademic' ? styles.dim : ''}`}>
                    College · admin or VPAF
                  </span>
                  <Plus size={14} aria-hidden />
                  <span className={`${styles.chainChip} ${styles.chainDept}`}>Department · admin or VPAF</span>
                  <Plus size={14} aria-hidden />
                  <span className={`${styles.chainChip} ${styles.chainProgram} ${scope.kind === 'NonAcademic' ? styles.dim : ''}`}>
                    Program · admin or VPAF
                  </span>
                  <ArrowRight size={14} aria-hidden />
                  <span className={styles.chainEnd}>one checklist</span>
                </div>
                <span className={styles.addsUpText}>
                  {addsUp} Lower levels can add documents, but can&apos;t change or remove ones required above them.
                </span>
              </div>

              {!modal && <InlineError error={draftOps.error} />}
              <div className={styles.footer}>
                <Button onClick={discard} disabled={!draft || busy}>
                  Discard draft
                </Button>
                <span className={styles.footerSpacer} />
                {justPublished && <span className={styles.published}>{justPublished}</span>}
                <Button icon={Eye} onClick={() => setModal('preview')}>
                  {previewLabel}
                </Button>
                <Button variant="primary" onClick={publish} disabled={!draft || busy}>
                  {draftOps.pending ? 'Saving…' : `Publish v${draftVersion}`}
                </Button>
              </div>
            </>
          )}
        </section>
      </div>

      {modal === 'add' && type && (
        <AddRequirementModal
          scope={scope}
          documentTypes={documentTypes}
          activeTerm={activeTerm}
          blocked={blocked}
          takenKeys={new Set(items.map((i) => i.key))}
          pending={draftOps.pending}
          error={draftOps.error}
          fieldErrors={slotFieldErrors(draftOps.error, items.length)}
          onClose={closeModal}
          onAdd={async (item) => {
            if (await editItems((all) => [...all, item])) setModal(null)
          }}
        />
      )}
      {modal === 'copy' && type && (
        <CopyModal
          scope={scope}
          submissionType={type}
          nonAcademicIds={new Set(tree.nonAcademic.map((n) => n.id))}
          blocked={blocked}
          pending={draftOps.pending}
          error={draftOps.error}
          onClose={closeModal}
          onCopy={async (copied) => {
            const saved = await editItems((all) => {
              const taken = new Set(all.map((i) => i.key ?? keyFromLabel(i.label)))
              const added = copied.map((r) => {
                const key = uniqueKey(r.key, taken)
                taken.add(key)
                return { ...toInput(r), key }
              })
              return [...all, ...added]
            })
            if (saved) setModal(null)
          }}
        />
      )}
      {modal === 'preview' && type && (
        <PreviewModal
          scope={scope}
          submissionType={type}
          documentTypes={documentTypes}
          activeTerm={activeTerm}
          inherited={inherited}
          items={items}
          isDraft={Boolean(draft)}
          title={previewLabel}
          onClose={closeModal}
        />
      )}
    </Frame>
  )
}

interface RequirementRowProps {
  item: RequirementItemDto
  documentTypes: DocumentTypeDto[]
  /** Code of the college or department that requires it; the row is read-only here. */
  inheritedFrom?: string
  readOnly?: boolean
  disabled?: boolean
  error?: string
  isNew?: boolean
  newLabel?: string
  dragging?: boolean
  dropTarget?: boolean
  onRule?: (rule: RequirementRule) => void
  onFiles?: (files: FileLimit) => void
  onRemove?: () => void
  onDragStart?: () => void
  onDragOver?: () => void
  onDrop?: () => void
  onDragEnd?: () => void
}

function RequirementRow({
  item,
  documentTypes,
  inheritedFrom,
  readOnly: viewOnly,
  disabled,
  error,
  isNew,
  newLabel,
  dragging,
  dropTarget,
  onRule,
  onFiles,
  onRemove,
  onDragStart,
  onDragOver,
  onDrop,
  onDragEnd,
}: RequirementRowProps) {
  const tag = documentTypes.find((t) => t.id === item.documentTypeId)?.tag
  const inherited = Boolean(inheritedFrom)
  const readOnly = inherited || Boolean(viewOnly)
  return (
    <div
      className={[
        styles.reqRow,
        isNew ? styles.reqNew : '',
        inherited ? styles.reqInherited : '',
        dragging ? styles.dragging : '',
        dropTarget ? styles.dropTarget : '',
      ].join(' ')}
      draggable={!readOnly && !disabled}
      onDragStart={(e) => {
        e.dataTransfer.effectAllowed = 'move'
        onDragStart?.()
      }}
      onDragOver={(e) => {
        if (readOnly) return
        e.preventDefault()
        onDragOver?.()
      }}
      onDrop={(e) => {
        e.preventDefault()
        onDrop?.()
      }}
      onDragEnd={onDragEnd}
    >
      {inherited ? (
        <Lock size={14} className={styles.lock} aria-label={`Required by ${inheritedFrom}`} />
      ) : readOnly ? (
        <span className={styles.gripSpace} />
      ) : (
        <GripVertical size={14} className={styles.grip} aria-label="Drag to reorder" />
      )}
      <span className={`${styles.reqLabel} ${panel.fill}`}>
        <span className={styles.reqTitleRow}>
          <span className={styles.reqTitle}>{item.label}</span>
          {isNew && <Badge tone="success">{newLabel}</Badge>}
        </span>
        <span className={styles.reqKey}>
          key: {item.key}
          {inherited && ` · required by ${inheritedFrom}, read-only here`}
        </span>
        {error && <span className={panel.error}>{error}</span>}
      </span>
      <span className={styles.colType}>
        <TypeChip label={`${item.documentTypeName} · ${item.documentTypeCode}`} />
        {tag && <TagChip tag={tag} label={tag === 'None' ? 'None' : tag} />}
      </span>
      <span className={`${panel.cell} ${styles.colRule}`}>
        <Segmented
          size="sm"
          options={ruleOptions}
          value={item.rule}
          onChange={(rule) => onRule?.(rule)}
          disabled={readOnly || disabled}
          ariaLabel={`Rule for ${item.label}`}
        />
      </span>
      <span className={`${panel.cell} ${styles.colFiles}`}>
        {readOnly ? (
          filesLabel(item.rule, item.files)
        ) : (
          <select
            className={styles.filesSelect}
            value={fileValue(item.files)}
            disabled={disabled}
            onChange={(e) => onFiles?.(parseFiles(e.target.value))}
            aria-label={`Files for ${item.label}`}
          >
            {fileChoices(item.rule, item.files).map((f) => (
              <option key={fileValue(f)} value={fileValue(f)}>
                {filesLabel(item.rule, f)}
              </option>
            ))}
          </select>
        )}
      </span>
      {readOnly ? (
        <span className={styles.actionSpace} />
      ) : (
        <button type="button" className={styles.remove} disabled={disabled} onClick={onRemove} aria-label={`Remove ${item.label}`}>
          <X size={16} />
        </button>
      )}
    </div>
  )
}

const tagNote = (type: DocumentTypeDto | undefined, term: AcademicTermDto | undefined) => {
  if (!type) return 'Pick a document type first. Its tag decides the year or term folder.'
  if (type.tag === 'Term') return `Term type: filed under ${term ? `${term.academicYearName} / ${term.name}` : 'the submission’s year and term'}.`
  if (type.tag === 'Year') return `Year type: filed under ${term?.academicYearName ?? 'the submission’s year'}.`
  return 'No period tag: filed directly in the submitter’s folder.'
}

interface AddRequirementModalProps {
  scope: Scope
  documentTypes: DocumentTypeDto[]
  activeTerm?: AcademicTermDto
  blocked: Map<string, string>
  takenKeys: ReadonlySet<string>
  pending: boolean
  error: unknown
  fieldErrors: Record<string, string>
  onClose: () => void
  onAdd: (item: RequirementItemInput) => void
}

function AddRequirementModal({
  scope,
  documentTypes,
  activeTerm,
  blocked,
  takenKeys,
  pending,
  error,
  fieldErrors,
  onClose,
  onAdd,
}: AddRequirementModalProps) {
  const fits = scope.kind === 'NonAcademic' ? 'NonAcademic' : 'Academic'
  const options = documentTypes.filter((t) => t.active && (t.scope === 'All' || t.scope === fits))
  const [typeId, setTypeId] = useState('')
  const [label, setLabel] = useState('')
  const [labelTouched, setLabelTouched] = useState(false)
  const [rule, setRule] = useState<RequirementRule>('Required')
  const [files, setFiles] = useState<FileLimit>({ min: 1, max: 1 })

  const type = options.find((t) => t.id === typeId)
  const effectiveLabel = labelTouched ? label : (type?.name ?? '')
  const key = uniqueKey(keyFromLabel(effectiveLabel) || (type?.code.toLowerCase() ?? ''), takenKeys)
  const slotFiles = filesFor(rule, files)

  const add = () => {
    if (!type || !effectiveLabel.trim()) return
    onAdd({ key, label: effectiveLabel.trim(), documentTypeId: type.id, rule, files: slotFiles, due: null })
  }

  const hasFieldErrors = Object.keys(fieldErrors).length > 0

  return (
    <Modal
      title={`Add requirement for ${scope.name}`}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" onClick={add} disabled={!type || !effectiveLabel.trim() || pending}>
            {pending ? 'Saving…' : 'Add requirement'}
          </Button>
        </>
      }
    >
      <label className={panel.field}>
        <span className={panel.label}>Document type</span>
        <select className={panel.select} value={typeId} onChange={(e) => setTypeId(e.target.value)} autoFocus>
          <option value="" disabled>
            Choose a type…
          </option>
          {options.map((t) => (
            <option key={t.id} value={t.id} disabled={blocked.has(t.id)}>
              {t.name} · {t.code}
              {blocked.has(t.id) ? ` (${blocked.get(t.id)})` : ''}
            </option>
          ))}
        </select>
        <span className={panel.help}>{tagNote(type, activeTerm)}</span>
        {fieldErrors.documentTypeId && <span className={panel.error}>{fieldErrors.documentTypeId}</span>}
      </label>
      <label className={panel.field}>
        <span className={panel.label}>Label shown in the checklist</span>
        <input
          className={panel.input}
          value={effectiveLabel}
          placeholder="e.g. Accomplishment report"
          onChange={(e) => {
            setLabelTouched(true)
            setLabel(e.target.value)
          }}
        />
        {effectiveLabel && <span className={panel.help}>key: {key}</span>}
        {(fieldErrors.label || fieldErrors.key) && (
          <span className={panel.error}>{[fieldErrors.label, fieldErrors.key].filter(Boolean).join(' ')}</span>
        )}
      </label>
      <div className={panel.fieldRow}>
        <div className={panel.field}>
          <span className={panel.label}>Rule</span>
          <Segmented size="sm" options={ruleOptions} value={rule} onChange={setRule} ariaLabel="Rule" />
        </div>
        <label className={`${panel.field} ${panel.fill}`}>
          <span className={panel.label}>Files</span>
          <select className={panel.select} value={fileValue(slotFiles)} onChange={(e) => setFiles(parseFiles(e.target.value))}>
            {fileChoices(rule, slotFiles).map((f) => (
              <option key={fileValue(f)} value={fileValue(f)}>
                {filesLabel(rule, f)}
              </option>
            ))}
          </select>
          {fieldErrors.files && <span className={panel.error}>{fieldErrors.files}</span>}
        </label>
      </div>
      <InlineError error={fieldErrors.slot ? new Error(fieldErrors.slot) : hasFieldErrors ? undefined : error} />
    </Modal>
  )
}

interface CopyModalProps {
  scope: Scope
  submissionType: SubmissionTypeRefDto
  nonAcademicIds: ReadonlySet<string>
  blocked: Map<string, string>
  pending: boolean
  error: unknown
  onClose: () => void
  onCopy: (items: RequirementItemDto[]) => void
}

function CopyModal({ scope, submissionType, nonAcademicIds, blocked, pending, error, onClose, onCopy }: CopyModalProps) {
  const copySources = useApi((signal) => getCopySources(submissionType.id, signal), [submissionType.id])
  // Published lists of other colleges or departments of the same kind (programs copy among themselves).
  const isNonAcademic = scope.kind === 'NonAcademic'
  const sources = (copySources.data ?? []).filter(
    (s) => s.scope.type !== 'Program' && s.scope.id !== scope.node.id && nonAcademicIds.has(s.scope.id) === isNonAcademic,
  )
  const [picked, setPicked] = useState<string | null>(null)
  const source = sources.find((s) => s.id === picked) ?? sources[0]
  const copyable = (source?.items ?? []).filter((r) => !blocked.has(r.documentTypeId))
  const skipped = (source?.items ?? []).filter((r) => blocked.has(r.documentTypeId))

  return (
    <Modal
      title="Copy from another college or department"
      description={`Copies the “${submissionType.name}” list into the ${scope.name} draft. Types already required here or above are skipped.`}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" disabled={copyable.length === 0 || pending} onClick={() => onCopy(copyable)}>
            Copy {copyable.length} document{copyable.length === 1 ? '' : 's'}
          </Button>
        </>
      }
    >
      {copySources.error ? (
        <ErrorState error={copySources.error} onRetry={copySources.reload} compact />
      ) : !copySources.data ? (
        <Loading compact />
      ) : sources.length === 0 ? (
        <p className={panel.help}>
          No other {isNonAcademic ? 'non-academic department' : 'college or department'} has a published “{submissionType.name}”
          list yet.
        </p>
      ) : (
        <div className={styles.sourceList} role="radiogroup">
          {sources.map((s) => (
            <button
              key={s.id}
              type="button"
              role="radio"
              aria-checked={s.id === source?.id}
              className={`${styles.sourceOption} ${s.id === source?.id ? styles.sourceSelected : ''}`}
              onClick={() => setPicked(s.id)}
            >
              {s.scope.type === 'College' ? <Building2 size={16} aria-hidden /> : <Building size={16} aria-hidden />}
              {s.scope.name}
              <span className={styles.sourceItems}>{s.items.map((r) => r.label).join(', ')}</span>
            </button>
          ))}
        </div>
      )}
      {skipped.length > 0 && (
        <p className={panel.help}>Skipped: {skipped.map((r) => `${r.label} (${blocked.get(r.documentTypeId)})`).join(', ')}</p>
      )}
      <InlineError error={error} />
    </Modal>
  )
}

interface PreviewModalProps {
  scope: Scope
  submissionType: SubmissionTypeRefDto
  documentTypes: DocumentTypeDto[]
  activeTerm?: AcademicTermDto
  inherited: RequirementItemDto[]
  items: RequirementItemDto[]
  isDraft: boolean
  title: string
  onClose: () => void
}

/** Someone the list applies to, and the folder their documents land in. */
async function sampleSubmitter(scope: Scope, signal: AbortSignal): Promise<{ name: string; folder: string[] }> {
  if (scope.kind === 'NonAcademic') {
    const dept = await getDepartment(scope.node.id, signal)
    const name = dept.staff[0]?.person.displayName ?? '{staff member}'
    return { name, folder: ['Non-academic departments', scope.name, name] }
  }
  const program = programsUnder(scope).find((p) => p.facultyCount > 0)
  const college = scope.college?.name ?? scope.node.name
  if (!program) return { name: '{faculty member}', folder: ['Colleges', college, '{department}', '{program}', '{faculty member}'] }
  const detail = await getProgram(program.id, signal)
  const member = detail.faculty.find((f) => f.isPrimary) ?? detail.faculty[0]
  const department = scope.college?.departments.find((d) => d.id === program.departmentId)
  const name = member?.person.displayName ?? '{faculty member}'
  return { name, folder: ['Colleges', detail.college.name, department?.shortName ?? detail.department.name, detail.name, name] }
}

function PreviewModal({ scope, submissionType, documentTypes, activeTerm, inherited, items, isDraft, title, onClose }: PreviewModalProps) {
  const sample = useApi((signal) => sampleSubmitter(scope, signal), [scope.node.id])
  const ownCode = scope.node.code
  const rows = [
    ...inherited.map((r) => ({ r, source: r.source?.code ?? 'above' })),
    ...items.map((r) => ({ r, source: ownCode })),
  ]
  const who = sample.data?.name ?? (scope.kind === 'NonAcademic' ? '{staff member}' : '{faculty member}')
  const tagOf = (id: string) => documentTypes.find((t) => t.id === id)?.tag

  return (
    <Modal
      title={title}
      description={`The checklist ${who} sees when starting a “${submissionType.name}” submission${activeTerm ? ` in ${termLabel(activeTerm)}` : ''}.${isDraft ? ' Draft changes included.' : ''}`}
      width={640}
      onClose={onClose}
      footer={<Button onClick={onClose}>Close</Button>}
    >
      {rows.length === 0 ? (
        <p className={panel.help}>Nothing is required at this level yet.</p>
      ) : (
        <div className={styles.preview}>
          {rows.map(({ r, source }) => (
            <div key={`${source}-${r.key}`} className={styles.previewRow}>
              <div className={styles.previewTop}>
                <span className={styles.reqTitle}>{r.label}</span>
                <TypeChip label={`${r.documentTypeName} · ${r.documentTypeCode}`} />
                <Badge tone={r.rule === 'Required' ? 'info' : 'neutral'}>{r.rule}</Badge>
                <span className={styles.previewSource}>
                  {filesLabel(r.rule, r.files)} · from {source}
                </span>
              </div>
              {sample.data && (
                <span className={styles.previewPath}>
                  <Route size={12} aria-hidden />
                  {folderPath(sample.data.folder, tagOf(r.documentTypeId), activeTerm).join(' / ')}
                </span>
              )}
            </div>
          ))}
        </div>
      )}
      {sample.error ? <InlineError error={sample.error} /> : null}
      {scope.kind !== 'NonAcademic' && (
        <p className={panel.help}>Faculty also get their program&apos;s own list.</p>
      )}
    </Modal>
  )
}

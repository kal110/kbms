import { useState } from 'react'
import type { DragEvent } from 'react'
import {
  CalendarRange,
  CircleCheck,
  CircleDashed,
  GraduationCap,
  GripVertical,
  Info,
  Lock,
  Pencil,
  Plus,
  X,
} from 'lucide-react'
import { getOrgTree } from '../../api/org'
import { getAcademicYears, getDocumentTypes } from '../../api/reference'
import { getCopySources, getRequirementScopeTree, getScopeRequirementSets } from '../../api/requirements'
import type {
  AcademicYearDto,
  DocumentTypeDto,
  OrgProgramDto,
  OrgTreeDto,
  RequirementItemDto,
  RequirementItemInput,
  RequirementRule,
  RequirementScopeNodeDto,
  RequirementSetDto,
  ScopeRequirementSetsDto,
  SubmissionTypeRefDto,
  UnitRefDto,
} from '../../api/types'
import { useApi } from '../../api/useApi'
import type { ApiState } from '../../api/useApi'
import { can } from '../../app/navigation'
import { useMe } from '../../auth/SessionContext'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { TagChip, TypeChip } from '../../components/Chips'
import { Modal } from '../../components/Modal'
import { Segmented } from '../../components/Segmented'
import { longDate, shortDate } from '../../lib/format'
import { RequirementModal } from './RequirementModal'
import { RequirementTabs } from './RequirementsFrame'
import {
  diffRequirements,
  errorsByKey,
  filesFor,
  filesLabel,
  findTerm,
  lowerFirst,
  openTerms,
  ruleOptions,
  slotFieldErrors,
  termName,
  toInput,
} from './requirementsUi'
import { useRequirementDraft } from './useRequirementDraft'
import styles from './RequiredDocumentsPage.module.css'

type Dialog =
  | { kind: 'add' }
  | { kind: 'edit'; key: string }
  | { kind: 'preview' }
  | { kind: 'copy' }
  | { kind: 'discard' }

const programOf = (org: OrgTreeDto | undefined, id: string) =>
  org?.colleges.flatMap((c) => c.departments.flatMap((d) => d.programs)).find((p) => p.id === id)

/** A program in the Program select; `group` is its college when the caller picks from every program. */
interface ProgramChoice extends UnitRefDto {
  group?: string
}

/** Programs in the requirement scope tree: colleges → departments → programs for the VPAF and admin, flat for an AS. */
function programChoices(nodes: RequirementScopeNodeDto[], group?: string): ProgramChoice[] {
  return nodes.flatMap((node) =>
    node.scopeType === 'Program'
      ? [{ id: node.id, code: node.code, name: node.name, group }]
      : programChoices(node.children, node.scopeType === 'College' ? node.name : group),
  )
}

/**
 * Each program's checklist per submission type (frame 18). The VPAF and the System Administrator set it; an
 * Academic Supervisor reads their program's list and verifies faculty submissions against it.
 */
export function RequiredDocumentsPage() {
  const me = useMe()
  const manager = can(me, 'canSetProgramRequirements')
  const allowed = manager || can(me, 'canViewProgramRequirements')
  const tree = useApi((signal) => (allowed ? getRequirementScopeTree(signal) : Promise.resolve(undefined)), [allowed])
  const [programId, setProgramId] = useState<string | null>(null)

  const programs = tree.data ? programChoices(tree.data.academic) : []
  const program = programs.find((p) => p.id === programId) ?? programs[0]

  if (!allowed || !program) {
    return (
      <>
        <Topbar manager={manager} />
        <div className={styles.body}>
          {allowed && !tree.data ? (
            tree.error ? (
              <ErrorState error={tree.error} onRetry={tree.reload} />
            ) : (
              <Loading label="Loading programs…" />
            )
          ) : (
            <p className={styles.muted12}>
              {allowed
                ? 'There are no programs yet.'
                : 'Each program’s required documents are set by the VPAF. Its Academic Supervisor checks submissions against them.'}
            </p>
          )}
        </div>
      </>
    )
  }
  return (
    <ProgramRequirements key={program.id} program={program} programs={programs} onProgram={setProgramId} manager={manager} />
  )
}

interface TopbarProps {
  program?: UnitRefDto
  programs?: ProgramChoice[]
  onProgram?: (id: string) => void
  onCopy?: () => void
  /** The VPAF and admin: the copy button and the Programs | Colleges & departments tabs. */
  manager?: boolean
}

function Topbar({ program, programs = [], onProgram, onCopy, manager = false }: TopbarProps) {
  const groups = [...new Set(programs.map((p) => p.group))]
  const option = (p: ProgramChoice) => (
    <option key={p.id} value={p.id}>
      {p.name}
    </option>
  )
  return (
    <>
      <header className={styles.topbar}>
        <div className={styles.titleBlock}>
          <span className={styles.breadcrumb}>
            {programs.length > 1 && program && onProgram ? (
              <select
                className={styles.programSelect}
                aria-label="Program"
                value={program.id}
                onChange={(e) => onProgram(e.target.value)}
              >
                {groups.map((group) =>
                  group ? (
                    <optgroup key={group} label={group}>
                      {programs.filter((p) => p.group === group).map(option)}
                    </optgroup>
                  ) : (
                    programs.filter((p) => !p.group).map(option)
                  ),
                )}
              </select>
            ) : (
              (program?.name ?? 'Program')
            )}
            {'  /  Required documents'}
          </span>
          <h1 className={styles.title}>Required documents</h1>
        </div>
        {manager && (
          <Button disabled={!onCopy} onClick={onCopy}>
            Copy from another program
          </Button>
        )}
      </header>
      {manager && <RequirementTabs />}
    </>
  )
}

interface ProgramRequirementsProps {
  program: UnitRefDto
  programs: ProgramChoice[]
  onProgram: (id: string) => void
  manager: boolean
}

function ProgramRequirements({ program, programs, onProgram, manager }: ProgramRequirementsProps) {
  const sets = useApi((signal) => getScopeRequirementSets('Program', program.id, signal), [program.id])
  const reference = useApi(() => Promise.all([getDocumentTypes(true), getAcademicYears(), getOrgTree()]), [])

  const failed = (!sets.data && sets.error) || (!reference.data && reference.error)
  if (failed || !sets.data || !reference.data) {
    return (
      <>
        <Topbar program={program} programs={programs} onProgram={onProgram} manager={manager} />
        <div className={styles.body}>
          {failed ? (
            <ErrorState
              error={sets.error ?? reference.error}
              onRetry={() => {
                sets.reload()
                reference.reload()
              }}
            />
          ) : (
            <Loading label="Loading required documents…" />
          )}
        </div>
      </>
    )
  }

  const [documentTypes, years, org] = reference.data
  return (
    <ProgramEditor
      program={program}
      programs={programs}
      onProgram={onProgram}
      manager={manager}
      data={sets.data}
      setData={sets.setData}
      reload={sets.reload}
      documentTypes={documentTypes}
      years={years}
      orgProgram={programOf(org, program.id)}
      allPrograms={org.colleges.flatMap((c) => c.departments.flatMap((d) => d.programs))}
    />
  )
}

interface ProgramEditorProps extends ProgramRequirementsProps {
  data: ScopeRequirementSetsDto
  setData: ApiState<ScopeRequirementSetsDto>['setData']
  reload: () => void
  documentTypes: DocumentTypeDto[]
  years: AcademicYearDto[]
  orgProgram?: OrgProgramDto
  allPrograms: OrgProgramDto[]
}

function ProgramEditor({
  program,
  programs,
  onProgram,
  manager,
  data,
  setData,
  reload,
  documentTypes,
  years,
  orgProgram,
  allPrograms,
}: ProgramEditorProps) {
  const me = useMe()
  const draftOps = useRequirementDraft('Program', program.id, setData, reload)
  const editable = data.canManage && can(me, 'canSetProgramRequirements')

  // Every type the program can have a list for, plus any type that still has one.
  const types: SubmissionTypeRefDto[] = [
    ...data.availableTypes,
    ...data.sets.map((g) => g.submissionType).filter((t) => !data.availableTypes.some((a) => a.id === t.id)),
  ]
  const groupOf = (typeId: string) => data.sets.find((g) => g.submissionType.id === typeId)
  // Open on the list being edited, else the first one with a published list.
  const defaultType = types.find((t) => groupOf(t.id)?.draft) ?? types.find((t) => groupOf(t.id)?.published) ?? types[0]

  const [typeId, setTypeId] = useState<string | null>(null)
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const [notice, setNotice] = useState('')
  const [dragKey, setDragKey] = useState<string | null>(null)
  const [overKey, setOverKey] = useState<string | null>(null)

  const type = types.find((t) => t.id === typeId) ?? defaultType
  if (!type) {
    return (
      <>
        <Topbar program={program} programs={programs} onProgram={onProgram} manager={manager} />
        <div className={styles.body}>
          <p className={styles.muted12}>No submission types are set up for programs yet. The System Administrator adds them in Settings.</p>
        </div>
      </>
    )
  }

  const group = groupOf(type.id)
  const published = group?.published ?? null
  const draft = group?.draft ?? null
  const items = draft?.items ?? published?.items ?? []
  const inherited = group?.inherited ?? []
  const changes = draft ? diffRequirements(published?.items ?? [], draft.items) : []
  const nextVersion = draft?.version ?? (published?.version ?? 0) + 1
  const activeTerm = years.flatMap((y) => y.terms).find((t) => t.status === 'Active')
  const busy = draftOps.pending || !editable
  const rowErrors = dialog ? {} : errorsByKey(draftOps.error, draftOps.sentKeys)

  const closeDialog = () => {
    setDialog(null)
    draftOps.clearError()
  }

  /** Every edit goes into the draft; the first edit starts one from the published list. */
  const editItems = (fn: (list: RequirementItemInput[]) => RequirementItemInput[]) => {
    setNotice('')
    return draftOps.edit(group, type.id, (req) => ({ ...req, items: fn(req.items) }))
  }

  const setRule = (key: string, rule: RequirementRule) =>
    editItems((list) => list.map((i) => (i.key === key ? { ...i, rule, files: filesFor(rule, i.files) } : i)))

  const remove = (key: string) => editItems((list) => list.filter((i) => i.key !== key))

  const save = async (req: RequirementItemInput) => {
    const saved = await editItems((list) =>
      list.some((i) => i.key === req.key) ? list.map((i) => (i.key === req.key ? req : i)) : [...list, req],
    )
    if (saved) setDialog(null)
    return Boolean(saved)
  }

  const move = (from: string, to: string) => {
    if (from === to) return
    editItems((list) => {
      const next = [...list]
      const [moved] = next.splice(
        next.findIndex((i) => i.key === from),
        1,
      )
      next.splice(
        next.findIndex((i) => i.key === to),
        0,
        moved,
      )
      return next
    })
  }

  const setEffectiveTerm = (termId: string) => {
    setNotice('')
    draftOps.edit(group, type.id, (req) => ({ ...req, effectiveFromTermId: termId || null }))
  }

  const publish = async () => {
    if (!draft) return
    const result = await draftOps.publish(draft)
    if (!result) return
    const start = findTerm(years, result.effectiveFrom?.id)?.startsOn
    setNotice(
      `v${result.version} published. ${program.code} faculty get it on new ${type.name.toLowerCase()} submissions from ${termName(result.effectiveFrom)}${start ? ` (${longDate(start)})` : ''}.${published ? ` Open submissions keep v${published.version}.` : ''}`,
    )
  }

  const discard = async () => {
    if (!draft) return
    if ((await draftOps.discard(draft)) === undefined) return
    setDialog(null)
    setNotice(published ? `Draft v${draft.version} discarded. v${published.version} stays current.` : `Draft v${draft.version} discarded.`)
  }

  const copy = async (source: RequirementSetDto) => {
    const copied = source.items.filter((i) => !inherited.some((h) => h.documentTypeId === i.documentTypeId)).map(toInput)
    setNotice('')
    const result = await draftOps.edit(group, type.id, (req) => ({ ...req, items: copied }), draft ? null : source.id)
    if (!result) return
    setDialog(null)
    setNotice(`Copied ${copied.length} requirements from ${source.scope.code} into draft v${result.version}. Review them, then publish.`)
  }

  const onDrop = (e: DragEvent, key: string) => {
    e.preventDefault()
    if (dragKey) move(dragKey, key)
    setDragKey(null)
    setOverKey(null)
  }

  const draftTerm = findTerm(years, draft?.effectiveFrom?.id)
  const effectiveStart = draftTerm?.startsOn
  const termOptions = openTerms(years)
  if (draftTerm && !termOptions.includes(draftTerm)) termOptions.unshift(draftTerm)
  const editing = dialog?.kind === 'edit' ? items.find((i) => i.key === dialog.key) : undefined
  const editIndex = editing ? items.indexOf(editing) : items.length
  const inheritedSources = [...new Set(inherited.map((i) => i.source?.name).filter(Boolean))].join(' and ')
  const typeTag = (id: string) => documentTypes.find((t) => t.id === id)?.tag
  const exampleTerm = draft?.effectiveFrom ?? published?.effectiveFrom ?? activeTerm

  return (
    <>
      <Topbar
        program={program}
        programs={programs}
        onProgram={onProgram}
        manager={manager}
        onCopy={editable ? () => setDialog({ kind: 'copy' }) : undefined}
      />

      <div className={styles.body}>
        <aside className={styles.types} aria-label="Submission types">
          <span className={styles.typesHead}>Submission types · {program.code}</span>
          {types.map((t) => {
            const g = groupOf(t.id)
            const parts = g?.published
              ? [`${g.published.items.length} requirements`, `v${g.published.version} published`]
              : [g?.draft ? 'Not published yet' : 'No list yet']
            if (g?.draft) parts.push(`v${g.draft.version} draft`)
            if (t.deanStage) parts.push('dean stage')
            return (
              <button
                key={t.id}
                type="button"
                className={`${styles.typeItem} ${t.id === type.id ? styles.typeItemActive : ''}`}
                aria-current={t.id === type.id}
                onClick={() => {
                  setTypeId(t.id)
                  setNotice('')
                  draftOps.clearError()
                }}
              >
                <span className={styles.typeName}>{t.name}</span>
                <span className={styles.typeSub}>{parts.join(' · ')}</span>
              </button>
            )
          })}
          <div className={styles.flex} />
          <p className={styles.hint}>
            {editable
              ? `The System Administrator manages document types and submission types in Settings. Lists for a whole college or department are under Colleges & departments; here you decide which other documents ${program.code} faculty submit for each type.`
              : `The VPAF sets which documents ${program.code} faculty submit for each type. You verify their submissions against this list in the Review queue.`}
          </p>
        </aside>

        <section className={styles.editor} aria-label={`${type.name} requirements`}>
          <div className={styles.editorHead}>
            <h2 className={styles.editorTitle}>{type.name}</h2>
            {draft && <Badge tone="warning">Draft v{draft.version}</Badge>}
            {published ? <Badge tone="success">v{published.version} published</Badge> : <Badge tone="neutral">Not published</Badge>}
            <span className={styles.flex} />
            <span className={styles.muted12}>
              {program.name}
              {orgProgram ? ` · ${orgProgram.facultyCount} faculty` : ''}
            </span>
          </div>

          {notice && (
            <div className={styles.notice} role="status">
              <CircleCheck size={16} aria-hidden />
              <span>{notice}</span>
              <button type="button" className={styles.noticeClose} aria-label="Dismiss" onClick={() => setNotice('')}>
                <X size={14} />
              </button>
            </div>
          )}

          <div className={styles.versions}>
            {published ? (
              <div className={styles.versionPublished}>
                <Lock size={16} aria-hidden className={styles.iconSuccess} />
                <span className={styles.versionText}>
                  <span className={styles.versionTitle}>
                    v{published.version} · Published {longDate(published.publishedAt)}
                    {published.publishedBy ? ` by ${published.publishedBy.displayName}` : ''} · {published.items.length} requirements
                  </span>
                  <span className={styles.versionSub}>
                    Effective {termName(published.effectiveFrom)}
                    {published.openSubmissions > 0
                      ? ` · kept by ${published.openSubmissions} ${published.openSubmissions === 1 ? 'open submission until it closes' : 'open submissions until they close'}`
                      : ' · used by new submissions'}
                  </span>
                </span>
              </div>
            ) : (
              <div className={styles.versionNone}>
                <Lock size={16} aria-hidden className={styles.iconMuted} />
                <span className={styles.versionText}>
                  <span className={styles.versionTitle}>Nothing published yet</span>
                  <span className={styles.versionSub}>
                    {program.code} faculty get only the documents required above the program until v1 is published
                  </span>
                </span>
              </div>
            )}
            {draft ? (
              <div className={styles.versionDraft}>
                <Pencil size={16} aria-hidden className={styles.iconWarning} />
                <span className={styles.versionText}>
                  <span className={styles.versionTitle}>
                    v{draft.version} · Draft · {editable ? 'editing now' : 'not published yet'}
                  </span>
                  <span className={styles.versionSub}>
                    Effective from {termName(draft.effectiveFrom)}
                    {effectiveStart ? ` · new submissions from ${longDate(effectiveStart)}` : ''}
                  </span>
                </span>
                <label className={styles.termSelect}>
                  <CalendarRange size={13} aria-hidden />
                  <span className="visually-hidden">Effective term</span>
                  <select value={draft.effectiveFrom?.id ?? ''} disabled={busy} onChange={(e) => setEffectiveTerm(e.target.value)}>
                    {!draft.effectiveFrom && <option value="">Pick a term</option>}
                    {termOptions.map((t) => (
                      <option key={t.id} value={t.id}>
                        {termName(t).replace('AY ', '')}
                      </option>
                    ))}
                  </select>
                </label>
              </div>
            ) : (
              <div className={styles.versionNone}>
                <Pencil size={16} aria-hidden className={styles.iconMuted} />
                <span className={styles.versionText}>
                  <span className={styles.versionTitle}>No draft</span>
                  <span className={styles.versionSub}>
                    {editable ? `Any change below starts v${nextVersion}` : 'View only: the VPAF sets this list'}
                  </span>
                </span>
              </div>
            )}
          </div>

          <div className={styles.appliesTo}>
            <span className={styles.appliesLabel}>Applies to</span>
            <span className={styles.lockedSelect}>
              <GraduationCap size={13} aria-hidden />
              {program.name}
              {editable ? '' : ' (view only)'}
              <Lock size={13} aria-hidden />
            </span>
            <span className={styles.muted12}>
              {program.code} faculty get this checklist when they start a {type.name} submission.
            </span>
          </div>

          {inherited.length > 0 && (
            <div className={styles.inherited}>
              <div className={styles.inheritedLabel}>
                <Lock size={14} aria-hidden />
                ALSO REQUIRED — from {inheritedSources || 'a broader list'}. Read-only here
                {editable ? ': change it under Colleges & departments.' : '.'}
              </div>
              {inherited.map((item) => (
                <InheritedRow key={`${item.source?.id}-${item.key}`} item={item} tag={typeTag(item.documentTypeId)} />
              ))}
            </div>
          )}

          <div className={styles.table}>
            <div className={`${styles.tableRow} ${styles.tableHead}`}>
              <span className={styles.gripSpace} />
              <span className={styles.colLabel}>
                Required document <span className={styles.noCase}> (label · key)</span>
              </span>
              <span className={styles.colType}>
                Document type <span className={styles.noCase}>(tag)</span>
              </span>
              <span className={styles.colRule}>Rule</span>
              <span className={styles.colFiles}>Files</span>
              <span className={styles.actionSpace} />
            </div>

            {items.length === 0 && (
              <div className={styles.tableRow}>
                <span className={styles.gripSpace} />
                <span className={styles.muted12}>No {program.code} requirements for {type.name.toLowerCase()} yet.</span>
              </div>
            )}

            {items.map((item) => {
              const tag = typeTag(item.documentTypeId)
              const isNew = Boolean(draft) && !published?.items.some((p) => p.key === item.key)
              const rowError = rowErrors[item.key]
              return (
                <div
                  key={item.key}
                  className={`${styles.tableRow} ${isNew ? styles.rowNew : ''} ${overKey === item.key && dragKey !== item.key ? styles.rowDropTarget : ''} ${dragKey === item.key ? styles.rowDragging : ''}`}
                  draggable={!busy}
                  onDragStart={(e) => {
                    setDragKey(item.key)
                    e.dataTransfer.effectAllowed = 'move'
                  }}
                  onDragOver={(e) => {
                    if (!dragKey) return
                    e.preventDefault()
                    setOverKey(item.key)
                  }}
                  onDragEnd={() => {
                    setDragKey(null)
                    setOverKey(null)
                  }}
                  onDrop={(e) => onDrop(e, item.key)}
                >
                  {editable ? <GripVertical size={14} aria-label="Drag to reorder" className={styles.grip} /> : <span className={styles.gripSpace} />}
                  <button
                    type="button"
                    className={styles.colLabel}
                    disabled={!editable}
                    onClick={() => setDialog({ kind: 'edit', key: item.key })}
                  >
                    <span className={styles.labelRow}>
                      <span className={styles.reqLabel}>{item.label}</span>
                      {isNew && <Badge tone="success">New in v{draft?.version}</Badge>}
                    </span>
                    <span className={styles.reqKey}>
                      key: {item.key}
                      {item.due ? ` · due ${shortDate(item.due)}` : ''}
                    </span>
                    {rowError && <span className={styles.formError}>{rowError}</span>}
                  </button>
                  <span className={styles.colType}>
                    <TypeChip label={`${item.documentTypeName} · ${item.documentTypeCode}`} />
                    {tag && <TagChip tag={tag} label={tag === 'None' ? 'None' : tag} />}
                  </span>
                  <span className={styles.colRule}>
                    <Segmented<RequirementRule>
                      ariaLabel={`${item.label} rule`}
                      size="sm"
                      options={ruleOptions}
                      value={item.rule}
                      disabled={busy}
                      onChange={(rule) => setRule(item.key, rule)}
                    />
                  </span>
                  <button
                    type="button"
                    className={`${styles.colFiles} ${styles.filesButton}`}
                    disabled={!editable}
                    onClick={() => setDialog({ kind: 'edit', key: item.key })}
                  >
                    {filesLabel(item.rule, item.files)}
                  </button>
                  {editable ? (
                    <button
                      type="button"
                      className={styles.removeButton}
                      aria-label={`Remove ${item.label}`}
                      disabled={busy}
                      onClick={() => remove(item.key)}
                    >
                      <X size={16} />
                    </button>
                  ) : (
                    <span className={styles.actionSpace} />
                  )}
                </div>
              )
            })}

            {editable && (
              <button type="button" className={styles.addRow} disabled={busy} onClick={() => setDialog({ kind: 'add' })}>
                <Plus size={15} aria-hidden />
                <span className={styles.addLabel}>Add requirement</span>
                <span className={styles.muted12}>— pick a document type first; the label defaults to the type name and can be renamed</span>
              </button>
            )}
          </div>

          <div className={styles.info}>
            <Info size={16} aria-hidden className={styles.infoIcon} />
            <span>
              Faculty never choose a type. A file attached to a slot is tagged with that slot&apos;s document type, plus the
              submission&apos;s academic year and term (e.g. {type.name} · {termName(exampleTerm)}). A library document of another
              type, or another term, can&apos;t be attached to the slot.
            </span>
          </div>

          {draft && (
            <div className={styles.changes}>
              <span className="section-label">{published ? `Changes from v${published.version}` : 'In this first version'}</span>
              {changes.length === 0 && <span className={styles.muted12}>No changes yet.</span>}
              {changes.map((c) => (
                <span key={c.text} className={styles[`change_${c.kind}`]}>
                  {c.kind === 'added' ? '+' : c.kind === 'removed' ? '−' : '~'}  {c.text}
                </span>
              ))}
              <span className={styles.muted12}>
                {published ? `Submissions already open keep v${published.version}.` : ''}
                {effectiveStart ? ` Drafts started after ${longDate(effectiveStart)} use v${draft.version}.` : ''}
              </span>
            </div>
          )}

          <div className={styles.flex} />

          {!dialog && <InlineError error={draftOps.error} />}
          <footer className={styles.footer}>
            {editable && (
              <Button disabled={!draft || busy} onClick={() => setDialog({ kind: 'discard' })}>
                Discard draft
              </Button>
            )}
            <span className={styles.flex} />
            <Button onClick={() => setDialog({ kind: 'preview' })}>Preview as faculty</Button>
            {editable && (
              <Button variant="primary" disabled={!draft || changes.length === 0 || busy} onClick={publish}>
                {draftOps.pending ? 'Saving…' : `Publish v${nextVersion}`}
              </Button>
            )}
          </footer>
        </section>
      </div>

      {(dialog?.kind === 'add' || dialog?.kind === 'edit') && (
        <RequirementModal
          initial={editing}
          items={items}
          inherited={inherited}
          documentTypes={documentTypes}
          activeTerm={activeTerm}
          onSave={save}
          pending={draftOps.pending}
          error={draftOps.error}
          fieldErrors={slotFieldErrors(draftOps.error, editIndex)}
          onClose={closeDialog}
        />
      )}

      {dialog?.kind === 'preview' && (
        <Modal
          title={`What ${program.code} faculty see · ${type.name}`}
          description={
            draft || published
              ? `Requirement set v${(draft ?? published)?.version} (${draft ? 'draft' : 'published'}) · effective ${termName((draft ?? published)?.effectiveFrom)}`
              : 'No program list yet: only the documents required above the program.'
          }
          width={600}
          onClose={closeDialog}
          footer={<Button onClick={closeDialog}>Close</Button>}
        >
          <span className="section-label">Required documents</span>
          <div className={styles.previewList}>
            {[...inherited, ...items].map((item) => (
              <div key={`${item.source?.id ?? 'own'}-${item.key}`} className={styles.previewItem}>
                <CircleDashed size={18} aria-hidden className={styles.iconMuted} />
                <span className={styles.previewText}>
                  <span className={styles.reqLabel}>
                    {item.label} · {lowerFirst(item.rule)}
                  </span>
                  <span className={styles.reqKey}>
                    Not attached · {lowerFirst(filesLabel(item.rule, item.files))}
                    {item.due ? ` · due ${shortDate(item.due)}` : ''}
                  </span>
                </span>
                {item.source && <Badge tone="purple">From {item.source.code}</Badge>}
                <TypeChip label={item.documentTypeCode} />
              </div>
            ))}
            {inherited.length + items.length === 0 && <span className={styles.muted12}>Nothing is required yet.</span>}
          </div>
          <span className={styles.muted12}>
            Each file a faculty member attaches is tagged with the slot&apos;s type and their submission&apos;s term, then filed in
            their folder.
          </span>
        </Modal>
      )}

      {dialog?.kind === 'copy' && (
        <CopyModal
          programId={program.id}
          programs={allPrograms}
          submissionType={type}
          draftVersion={nextVersion}
          inherited={inherited}
          pending={draftOps.pending}
          error={draftOps.error}
          onClose={closeDialog}
          onCopy={copy}
        />
      )}

      {dialog?.kind === 'discard' && draft && (
        <Modal
          title={`Discard draft v${draft.version}?`}
          description={
            published
              ? `Your changes since v${published.version} will be lost. v${published.version} stays current.`
              : 'The draft will be deleted. Nothing has been published for this type yet.'
          }
          width={440}
          onClose={closeDialog}
          footer={
            <>
              <Button onClick={closeDialog}>Keep editing</Button>
              <Button variant="danger" disabled={draftOps.pending} onClick={discard}>
                Discard draft
              </Button>
            </>
          }
        >
          <span className={styles.muted12}>
            {changes.length} {changes.length === 1 ? 'change' : 'changes'} will be discarded.
          </span>
          <InlineError error={draftOps.error} />
        </Modal>
      )}
    </>
  )
}

function InheritedRow({ item, tag }: { item: RequirementItemDto; tag?: DocumentTypeDto['tag'] }) {
  return (
    <div className={styles.inheritedRow}>
      <span className={styles.reqLabel}>{item.label}</span>
      <span className={styles.flex} />
      <TypeChip label={`${item.documentTypeName} · ${item.documentTypeCode}`} />
      {tag && <TagChip tag={tag} label={tag === 'None' ? 'None' : tag} />}
      <span className={styles.inheritedRule}>
        {item.rule} · {lowerFirst(filesLabel(item.rule, item.files))}
      </span>
      {item.source && <Badge tone="purple">From {item.source.code}</Badge>}
    </div>
  )
}

interface CopyModalProps {
  programId: string
  programs: OrgProgramDto[]
  submissionType: SubmissionTypeRefDto
  draftVersion: number
  inherited: RequirementItemDto[]
  pending: boolean
  error: unknown
  onClose: () => void
  onCopy: (source: RequirementSetDto) => void
}

function CopyModal({ programId, programs, submissionType, draftVersion, inherited, pending, error, onClose, onCopy }: CopyModalProps) {
  const sources = useApi((signal) => getCopySources(submissionType.id, signal), [submissionType.id])
  const candidates = programs.filter((p) => p.id !== programId)
  const available = (id: string) => sources.data?.find((s) => s.scope.type === 'Program' && s.scope.id === id)
  const [picked, setPicked] = useState<string | null>(null)
  const from = picked ?? candidates.find((p) => available(p.id))?.id ?? ''
  const source = available(from)
  const copied = (source?.items ?? []).filter((i) => !inherited.some((h) => h.documentTypeId === i.documentTypeId))
  const typeName = submissionType.name

  return (
    <Modal
      title="Copy from another program"
      description={`Start draft v${draftVersion} of ${typeName} from another program's published set. This replaces the requirements in the draft.`}
      width={560}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" disabled={!source || pending} onClick={() => source && onCopy(source)}>
            Copy into draft v{draftVersion}
          </Button>
        </>
      }
    >
      {sources.error ? (
        <ErrorState error={sources.error} onRetry={sources.reload} compact />
      ) : !sources.data ? (
        <Loading compact />
      ) : (
        <div className={styles.copyList} role="radiogroup" aria-label="Program">
          {candidates.map((p) => {
            const set = available(p.id)
            return (
              <label
                key={p.id}
                className={`${styles.copyOption} ${from === p.id ? styles.copyOptionActive : ''} ${set ? '' : styles.copyOptionDisabled}`}
              >
                <input
                  type="radio"
                  name="copy-from"
                  className="visually-hidden"
                  disabled={!set}
                  checked={from === p.id}
                  onChange={() => setPicked(p.id)}
                />
                <GraduationCap size={16} aria-hidden className={styles.iconSuccess} />
                <span className={styles.previewText}>
                  <span className={styles.reqLabel}>{p.name}</span>
                  <span className={styles.reqKey}>
                    {set ? `${typeName} v${set.version} · ${set.items.length} requirements` : `No published ${typeName.toLowerCase()} set`}
                  </span>
                </span>
              </label>
            )
          })}
        </div>
      )}
      {source && (
        <div className={styles.previewList}>
          {copied.map((item) => (
            <div key={item.key} className={styles.previewItem}>
              <span className={styles.previewText}>
                <span className={styles.reqLabel}>
                  {item.label} · {lowerFirst(item.rule)}
                </span>
                <span className={styles.reqKey}>{filesLabel(item.rule, item.files)}</span>
              </span>
              <TypeChip label={item.documentTypeCode} />
            </div>
          ))}
        </div>
      )}
      <InlineError error={error} />
    </Modal>
  )
}

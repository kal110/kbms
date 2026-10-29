import { useEffect, useState } from 'react'
import type { LucideIcon } from 'lucide-react'
import {
  BookOpen,
  Building,
  Building2,
  Calendar,
  ChevronDown,
  ChevronRight,
  EyeOff,
  FileText,
  Folder as FolderIcon,
  FolderLock,
  FolderOpen,
  FolderSync,
  FolderTree,
  FolderUser,
  GraduationCap,
  Info,
  Route,
  Search,
  Shield,
  Users,
  X,
} from 'lucide-react'
import { Link, Navigate, useNavigate, useSearchParams } from 'react-router'
import { getFolder, getFolderChildren, getFolderTree, getMyFolders, searchDocuments } from '../../api/library'
import { getOrgTree } from '../../api/org'
import { getAcademicYears, getActiveTerm } from '../../api/reference'
import type { DocumentListItem, FolderKind, FolderNode, PersonDto } from '../../api/types'
import { useApi } from '../../api/useApi'
import { can } from '../../app/navigation'
import { useMe } from '../../auth/SessionContext'
import { ErrorState, Loading } from '../../components/AsyncState'
import { Badge } from '../../components/Badge'
import type { BadgeTone } from '../../components/Badge'
import { Button } from '../../components/Button'
import { TypeChip } from '../../components/Chips'
import { FilterChip } from '../../components/FilterChip'
import { Modal } from '../../components/Modal'
import { PageHeader } from '../../components/PageHeader'
import { stateLabel, stateTone } from './libraryUi'
import styles from './AcademicFoldersPage.module.css'

/** Direct documents listed per folder (the API's largest page). */
const FOLDER_PAGE_SIZE = 100

const kindIcon: Record<FolderKind, LucideIcon> = {
  AcademicRoot: FolderTree,
  OfficeRoot: FolderTree,
  College: Building2,
  Department: Building,
  Office: Building,
  Program: GraduationCap,
  Faculty: FolderUser,
  Staff: FolderUser,
  AcademicYear: Calendar,
  Term: FolderIcon,
  Course: BookOpen,
  Manual: FolderIcon,
}

const kindLabel: Record<FolderKind, string> = {
  AcademicRoot: 'Colleges root',
  OfficeRoot: 'Non-academic departments root',
  College: 'College folder',
  Department: 'Department folder',
  Office: 'Non-academic department folder',
  Program: 'Program folder',
  Faculty: 'Faculty folder',
  Staff: 'Staff folder',
  AcademicYear: 'Academic year folder',
  Term: 'Academic term folder',
  Course: 'Course folder',
  Manual: 'Folder',
}

const byOrder = (a: FolderNode, b: FolderNode) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name)
const isPersonKind = (kind: FolderKind) => kind === 'Faculty' || kind === 'Staff'

interface AccessRow {
  key: string
  icon: LucideIcon
  color: string
  who: string
  detail: string
  level: string
  tone: BadgeTone
  you?: boolean
}

const levelTone = (level: string): BadgeTone =>
  level === 'Manage' ? 'purple' : level === 'View' ? 'info' : level === 'Contribute' || level === 'Download' ? 'success' : 'neutral'

export function AcademicFoldersPage() {
  const navigate = useNavigate()
  const me = useMe()
  const isAdmin = can(me, 'isAdmin')
  const [params, setParams] = useSearchParams()

  const base = useApi(
    (signal) =>
      Promise.all([
        getFolderTree('all', signal),
        getOrgTree(),
        getMyFolders(signal),
        getAcademicYears(),
        getActiveTerm().catch(() => null),
      ]),
    [],
  )
  const [allNodes, org, mine, years, activeTerm] = base.data ?? [[], null, [], [], null]

  // The tree: system folders only (hand-made folders live in the Library).
  const nodes = allNodes.filter((f) => f.isSystem)
  const byId = new Map(nodes.map((f) => [f.id, f]))
  const children = new Map<string, FolderNode[]>()
  for (const f of nodes) {
    if (!f.parentId) continue
    const list = children.get(f.parentId) ?? []
    list.push(f)
    children.set(f.parentId, list)
  }
  for (const list of children.values()) list.sort(byOrder)
  const kidsOf = (id: string) => children.get(id) ?? []
  const tops = nodes.filter((f) => !f.parentId).sort(byOrder)

  const pathOf = (id: string) => {
    const out: FolderNode[] = []
    let cur = byId.get(id)
    while (cur) {
      out.unshift(cur)
      cur = cur.parentId ? byId.get(cur.parentId) : undefined
    }
    return out
  }
  // documentCount is per folder (the documents you can see directly in it); totals add up the subtree.
  const counts = new Map<string, number>()
  const countIn = (id: string): number => {
    const known = counts.get(id)
    if (known !== undefined) return known
    const total = (byId.get(id)?.documentCount ?? 0) + kidsOf(id).reduce((n, k) => n + countIn(k.id), 0)
    counts.set(id, total)
    return total
  }

  const colleges = new Map((org?.colleges ?? []).map((c) => [c.id, c]))
  const departments = new Map(
    [...(org?.colleges ?? []).flatMap((c) => c.departments), ...(org?.nonAcademicDepartments ?? [])].map((d) => [d.id, d]),
  )
  const programs = new Map([...departments.values()].flatMap((d) => d.programs).map((p) => [p.id, p]))
  const yearById = new Map(years.map((y) => [y.id, y]))
  const termById = new Map(years.flatMap((y) => y.terms).map((t) => [t.id, t]))

  /** The org units and period a folder sits in, from its path. */
  const contextOf = (f: FolderNode) => {
    const path = pathOf(f.id)
    const find = (...kinds: FolderKind[]) => path.find((p) => kinds.includes(p.kind))
    const ref = (...kinds: FolderKind[]) => find(...kinds)?.orgRefId ?? ''
    return {
      college: colleges.get(ref('College')),
      dept: departments.get(ref('Department', 'Office')),
      program: programs.get(ref('Program')),
      person: find('Faculty', 'Staff'),
      year: yearById.get(ref('AcademicYear')),
      term: termById.get(ref('Term')),
      course: find('Course'),
    }
  }

  /** Folders you reach in full whose parent you only see as a path. */
  const roots = nodes.filter((f) => f.accessible && !(f.parentId && byId.get(f.parentId)?.accessible)).sort(byOrder)

  /** Your own folder's current term, else the highest folder you manage, else your first folder. */
  const home = (() => {
    const person = mine.map((m) => byId.get(m.id)).find((f) => f !== undefined)
    if (person) {
      const year = kidsOf(person.id).find((k) => k.kind === 'AcademicYear' && k.orgRefId === activeTerm?.academicYearId)
      const term = year && kidsOf(year.id).find((k) => k.kind === 'Term' && k.orgRefId === activeTerm?.id)
      return term ?? year ?? person
    }
    const managed = nodes.filter((f) => f.myLevel === 'Manage').sort((a, b) => pathOf(a.id).length - pathOf(b.id).length)
    return managed[0] ?? roots[0] ?? tops[0]
  })()

  const termFilter = params.get('term') ?? ''
  const filterTerm = termFilter ? termById.get(termFilter) : undefined
  const terms = years.flatMap((y) => y.terms).sort((a, b) => b.startsOn.localeCompare(a.startsOn))
  const requested = params.get('folder') ?? undefined
  const requestedNode = requested ? byId.get(requested) : undefined
  const manualRequest = requested ? allNodes.find((f) => f.id === requested && !f.isSystem) : undefined
  const selected = requestedNode ?? home
  const selectedId = selected?.id
  const hiddenRequest = Boolean(base.data && requested && !requestedNode && !manualRequest)

  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [query, setQuery] = useState('')
  const [q, setQ] = useState('')
  const [infoOpen, setInfoOpen] = useState(false)
  // Opening a folder from a link (e.g. a document's breadcrumb) expands the tree down to it.
  const [expandedFor, setExpandedFor] = useState<string>()
  if (selectedId && expandedFor !== selectedId) {
    setExpandedFor(selectedId)
    setExpanded((prev) => new Set([...prev, ...pathOf(selectedId).map((f) => f.id)]))
  }

  useEffect(() => {
    const timer = window.setTimeout(() => setQ(query.trim()), 300)
    return () => window.clearTimeout(timer)
  }, [query])

  const scope = roots.length === 1 ? roots[0] : selectedId ? pathOf(selectedId)[0] : undefined
  const scopeId = scope?.id
  const scopeName = scope?.name ?? 'all academic folders'

  const detail = useApi((signal) => (selectedId ? getFolder(selectedId, signal) : Promise.resolve(null)), [selectedId])
  // With a term picked, the folder lists that term's documents from every folder below it.
  const contents = useApi(
    (signal) =>
      selectedId
        ? getFolderChildren(
            selectedId,
            termFilter ? { pageSize: FOLDER_PAGE_SIZE, termId: termFilter, includeSubfolders: true } : { pageSize: FOLDER_PAGE_SIZE },
            signal,
          )
        : Promise.resolve(null),
    [selectedId, termFilter],
  )
  const search = useApi(
    (signal) =>
      q && scopeId ? searchDocuments({ q, folderId: scopeId, termId: termFilter || undefined, pageSize: 50 }, signal) : Promise.resolve(null),
    [q, scopeId, termFilter],
  )

  if (manualRequest) return <Navigate to={`/library?folder=${manualRequest.id}`} replace />
  if (base.error && !base.data) {
    return (
      <>
        <PageHeader title="Academic folders" />
        <ErrorState error={base.error} onRetry={base.reload} />
      </>
    )
  }
  if (!base.data) {
    return (
      <>
        <PageHeader title="Academic folders" />
        <Loading label="Loading academic folders…" />
      </>
    )
  }

  const select = (id: string) => {
    setExpanded((prev) => new Set([...prev, ...pathOf(id).map((f) => f.id)]))
    setParams(termFilter ? { folder: id, term: termFilter } : { folder: id })
    setQuery('')
    setQ('')
  }
  const filterByTerm = (id: string) =>
    setParams((prev) => {
      const next = new URLSearchParams(prev)
      if (id) next.set('term', id)
      else next.delete('term')
      return next
    })
  const toggle = (id: string) =>
    setExpanded((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })

  const youChip = (person: PersonDto | null | undefined) => person?.id === me.id

  const chipFor = (f: FolderNode) => {
    if (f.kind === 'College') {
      const c = colleges.get(f.orgRefId ?? '')
      if (!c?.dean) return <span className={`${styles.chip} ${styles.chipNeutral}`}>no dean</span>
      return <span className={`${styles.chip} ${styles.chipPurple}`}>{youChip(c.dean) ? 'Dean · you' : 'Dean'}</span>
    }
    if (f.kind === 'Department') {
      const d = departments.get(f.orgRefId ?? '')
      if (!d?.head) return <span className={`${styles.chip} ${styles.chipNeutral}`}>no head</span>
      return <span className={`${styles.chip} ${styles.chipPurple}`}>{youChip(d.head) ? 'Head · you' : 'Head'}</span>
    }
    if (f.kind === 'Office') {
      const d = departments.get(f.orgRefId ?? '')
      const title = d?.headTitle ?? 'Head'
      if (!d?.head) return <span className={`${styles.chip} ${styles.chipNeutral}`}>{title} vacant</span>
      return <span className={`${styles.chip} ${styles.chipPurple}`}>{youChip(d.head) ? `${title} · you` : title}</span>
    }
    if (f.kind === 'Program') {
      const p = programs.get(f.orgRefId ?? '')
      if (!p?.supervisor) return <span className={`${styles.chip} ${styles.chipNeutral}`}>AS vacant</span>
      return <span className={`${styles.chip} ${styles.chipInfo}`}>{youChip(p.supervisor) ? 'AS · you' : 'AS'}</span>
    }
    if (isPersonKind(f.kind) && f.ownerUserId === me.id) return <span className={`${styles.chip} ${styles.chipSuccess}`}>you</span>
    return null
  }

  const iconColor = (f: FolderNode, isSel: boolean) => {
    if (isSel) return 'var(--primary)'
    switch (f.kind) {
      case 'College':
      case 'AcademicYear':
        return 'var(--purple-accent)'
      case 'Program':
        return 'var(--primary)'
      case 'Faculty':
      case 'Staff':
        return 'var(--success)'
      default:
        return 'var(--text-muted)'
    }
  }

  const selectedPath = selected ? pathOf(selected.id) : []

  const renderNode = (f: FolderNode, depth: number) => {
    const kids = kidsOf(f.id)
    const open = expanded.has(f.id) && kids.length > 0
    const isSel = f.id === selected?.id
    const onPath = selectedPath.some((p) => p.id === f.id)
    const Icon = f.kind === 'Term' && (isSel || open) ? FolderOpen : kindIcon[f.kind]
    const program = f.kind === 'Program' ? programs.get(f.orgRefId ?? '') : undefined
    const count = program
      ? program.facultyCount
      : isPersonKind(f.kind) || f.kind === 'AcademicYear' || f.kind === 'Term' || f.kind === 'Course'
        ? countIn(f.id)
        : undefined
    const more = program && f.accessible ? Math.max(0, program.facultyCount - kids.filter((k) => k.kind === 'Faculty').length) : 0
    const term = f.kind === 'Term' ? termById.get(f.orgRefId ?? '') : undefined
    return (
      <div key={f.id} className={styles.nodeGroup}>
        <div className={`${styles.node} ${isSel ? styles.nodeSelected : ''}`}>
          <span style={{ width: depth * 14 }} className={styles.indent} />
          {kids.length > 0 ? (
            <button
              type="button"
              className={styles.chevron}
              aria-label={open ? `Collapse ${f.name}` : `Expand ${f.name}`}
              onClick={() => toggle(f.id)}
            >
              {open ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
            </button>
          ) : (
            <span className={styles.chevronSpace} />
          )}
          <button type="button" className={styles.nodeMain} onClick={() => select(f.id)} aria-current={isSel ? 'true' : undefined}>
            <Icon size={16} style={{ color: iconColor(f, isSel) }} className={styles.nodeIcon} aria-hidden />
            <span className={`${styles.nodeName} ${onPath || f.kind === 'College' || f.kind === 'Program' ? styles.nodeStrong : ''}`}>
              {f.name}
            </span>
            {chipFor(f)}
            {term && <span className={styles.nodeHint}>{term.code}</span>}
            <span className={styles.flex} />
            {count !== undefined && <span className={`${styles.count} ${isSel ? styles.countSelected : ''}`}>{count}</span>}
          </button>
        </div>
        {open && kids.map((k) => renderNode(k, depth + 1))}
        {open && more > 0 && (
          <div className={styles.more} style={{ paddingLeft: 8 + (depth + 1) * 14 + 20 }}>
            + {more} more faculty
          </div>
        )}
      </div>
    )
  }

  const list = (items: (string | undefined)[]) =>
    new Intl.ListFormat('en', { type: 'conjunction' }).format(items.filter((x): x is string => Boolean(x)))
  const pathNames = nodes.filter((f) => !f.accessible && f.parentId).map((f) => f.name)
  const hint = isAdmin
    ? 'Admins see the whole tree to repair access, but not the documents in it.'
    : [
        'Only folders you can open are listed.',
        roots.length ? `You reach ${list(roots.map((r) => r.name))} through your role (${me.roleLabel}).` : '',
        pathNames.length ? `${list(pathNames)} show${pathNames.length === 1 ? 's' : ''} only as the path.` : '',
      ]
        .filter(Boolean)
        .join(' ')

  const tree = (
    <nav className={styles.tree} aria-label="Academic folders">
      <div className={styles.treeLabel}>
        <span>Academic folders</span>
        <span className={styles.flex} />
        <FolderSync size={13} aria-hidden />
        <span className={styles.treeSub}>from the org tree</span>
      </div>
      {/* The design lists the colleges directly; the roots show as nodes only when both trees are visible. */}
      {tops.length > 1 ? tops.map((t) => renderNode(t, 0)) : tops.flatMap((t) => kidsOf(t.id).map((c) => renderNode(c, 0)))}
      <span className={styles.treeSpacer} />
      <div className={styles.hint}>
        <EyeOff size={14} aria-hidden />
        <span>{hint}</span>
      </div>
    </nav>
  )

  if (!selected) {
    return (
      <>
        <PageHeader title="Academic folders" />
        <div className={styles.body}>
          {tree}
          <div className={styles.view}>
            <div className={styles.empty}>No academic or office folders are shared with you.</div>
          </div>
        </div>
      </>
    )
  }

  // Folder view
  const ctx = contextOf(selected)
  const { college, dept, program, person, year, term, course } = ctx
  const ownerName = person?.name
  const firstName = ownerName?.split(' ')[0] ?? ''
  const scopeCode = program?.code ?? dept?.shortName ?? dept?.name ?? ''
  const ownerRole = person ? (person.kind === 'Staff' ? `Staff, ${dept?.shortName ?? dept?.name ?? ''}` : `Faculty, ${program?.code ?? ''}`) : ''
  const pathOnlySelected = !selected.accessible
  const subfolders = kidsOf(selected.id)
  // While a new folder or term filter loads, the previous answer isn't shown under the new label.
  const folderDocs = !contents.loading && contents.data?.folder.id === selected.id ? contents.data.documents : null
  const hits = q && search.data?.query === q ? search.data : null
  const rows: DocumentListItem[] = q ? (hits?.items.map((h) => h.document) ?? []) : (folderDocs?.items ?? [])
  const filterLabel = filterTerm ? `${filterTerm.shortName} ${filterTerm.academicYearName.replace(/^AY\s*/, '')}` : ''
  const rowsLoading = q ? !hits && !search.error : !folderDocs && !contents.error
  const rowsError = q ? search.error : contents.error
  const directTotal = folderDocs?.total ?? 0
  const yearFolder = selected.kind === 'Term' && selected.parentId ? byId.get(selected.parentId) : undefined
  const yearDocs = yearFolder?.documentCount ?? 0
  const allowed = detail.data?.folder.id === selected.id ? detail.data.allowedActions : []
  const canManageAccess = allowed.includes('manageAccess')

  const mapping = () => {
    switch (selected.kind) {
      case 'Course':
        return `Filled automatically. When ${ownerName} submits a per-course type (a syllabus, a lab manual) for ${course?.name ?? 'this course'}, its term documents are filed here, inside ${term?.name ?? 'the term'}.`
      case 'Term':
        return `Filled automatically. When ${ownerName} submits for ${scopeCode}, each document is filed in ${firstName}'s folder under ${scopeCode} by its type's tag: Term types come here (per-course ones into a course folder), Year types go to ${year?.name ?? 'the year folder'}, untagged types go to ${ownerName}. Nobody picks a folder, and documents can't be uploaded here by hand.`
      case 'AcademicYear':
        return `Filled automatically. Year types submitted by ${ownerName} for ${year?.name ?? 'this year'} come here. Term types go into the term folders inside it.`
      case 'Faculty':
      case 'Staff':
        return 'Filled automatically. Documents whose type has no year or term come here. Year and Term types go into the year and term folders inside it.'
      case 'Program':
        return `One folder per faculty member of ${program?.name ?? selected.name}, created by the org sync. The Academic Supervisor sees all of them.`
      case 'Office':
        return `One folder per staff member of ${dept?.shortName ?? selected.name}, created by the org sync. The ${dept?.headTitle ?? 'Head'} sees all of them.`
      default:
        return 'Mirrors the org tree: College (Dean) → Department (no head) → Program (Academic Supervisor) → Faculty → academic year → term. Non-academic departments: Department (Head/Director) → Staff.'
    }
  }

  const footer = () => {
    if (isAdmin) return 'System administrators can repair access and re-run the org sync, but get no View on documents.'
    if (pathOnlySelected) return `You see this folder only as the path to ${roots.length ? list(roots.map((r) => r.name)) : 'your folders'}.`
    const parts: string[] = []
    if (filterTerm) {
      parts.push(
        directTotal === 0
          ? `No documents tagged ${filterLabel} in this folder or below.`
          : `${directTotal} document${directTotal === 1 ? '' : 's'} tagged ${filterLabel} in this folder and its subfolders.`,
      )
    } else if (selected.kind === 'Term' && term) {
      const label = `${term.shortName} ${year?.name.replace(/^AY\s*/, '') ?? ''}`.trim()
      parts.push(
        directTotal === 0
          ? `No documents tagged ${label} yet.`
          : `${directTotal} document${directTotal === 1 ? '' : 's'}, all tagged ${label}.`,
      )
      if (yearDocs > 0) parts.push(`${firstName}'s year-level documents (${yearDocs}) are one level up, in ${yearFolder?.name}.`)
    } else {
      parts.push(
        `${directTotal} document${directTotal === 1 ? '' : 's'} directly in this folder${subfolders.length ? `, ${subfolders.length} subfolder${subfolders.length === 1 ? '' : 's'}` : ''}.`,
      )
    }
    if (folderDocs && folderDocs.items.length < folderDocs.total) parts.push(`Showing the first ${folderDocs.items.length}.`)
    return parts.join(' ')
  }

  // Who gets what comes from the backend's system-folder matrix (SystemFolderGrants): grants go to
  // roles and memberships of the org units on the path, never to named people except the owner.
  const accessRows = (): AccessRow[] => {
    const out: AccessRow[] = []
    const holder = (p: PersonDto | null | undefined, role: string) => (p ? `${p.displayName} · ${role}` : `${role} (vacant)`)
    if (person) {
      out.push({
        key: 'owner',
        icon: FolderUser,
        color: 'var(--success)',
        who: `${person.name} · owner`,
        detail: `${kindLabel[person.kind]} · user:{${firstName.toLowerCase()}}`,
        level: 'Contribute',
        tone: levelTone('Contribute'),
        you: person.ownerUserId === me.id,
      })
      if (person.kind === 'Faculty') {
        if (program)
          out.push({
            key: 'as',
            icon: GraduationCap,
            color: 'var(--primary)',
            who: holder(program.supervisor, `Academic Supervisor, ${program.code}`),
            detail: `Program ${program.name} · as:{${program.code}}`,
            level: 'Download',
            tone: levelTone('Download'),
            you: youChip(program.supervisor),
          })
        if (college)
          out.push({
            key: 'dean',
            icon: Building2,
            color: 'var(--purple-accent)',
            who: holder(college.dean, `Dean, ${college.name}`),
            detail: `College ${college.code} · dean:{${college.code}}`,
            level: 'Download',
            tone: levelTone('Download'),
            you: youChip(college.dean),
          })
      } else if (dept) {
        const short = dept.shortName ?? dept.code
        out.push({
          key: 'head',
          icon: Building,
          color: 'var(--text-muted)',
          who: holder(dept.head, `${dept.headTitle ?? 'Head'}, ${short}`),
          detail: `Department ${short} · head:{${short}}`,
          level: 'Download',
          tone: levelTone('Download'),
          you: youChip(dept.head),
        })
      }
    } else if (selected.kind === 'Office' && dept) {
      const short = dept.shortName ?? dept.code
      out.push(
        {
          key: 'head',
          icon: Building,
          color: 'var(--text-muted)',
          who: holder(dept.head, `${dept.headTitle ?? 'Head'}, ${short}`),
          detail: `Department ${short} · head:{${short}}`,
          level: 'Manage',
          tone: levelTone('Manage'),
          you: youChip(dept.head),
        },
        {
          key: 'members',
          icon: Users,
          color: 'var(--text-muted)',
          who: `${short} staff`,
          detail: `Everyone in the department · dept:{${short}}`,
          level: 'View',
          tone: levelTone('View'),
        },
      )
    } else if (college) {
      // College, Department and Program folders inherit down the chain.
      out.push({
        key: 'dean',
        icon: Building2,
        color: 'var(--purple-accent)',
        who: holder(college.dean, `Dean, ${college.name}`),
        detail: `College ${college.code} · dean:{${college.code}}`,
        level: 'Manage',
        tone: levelTone('Manage'),
        you: youChip(college.dean),
      })
      if (program)
        out.push({
          key: 'as',
          icon: GraduationCap,
          color: 'var(--primary)',
          who: holder(program.supervisor, `Academic Supervisor, ${program.code}`),
          detail: `Program ${program.name} · as:{${program.code}}`,
          level: 'Manage',
          tone: levelTone('Manage'),
          you: youChip(program.supervisor),
        })
      out.push({
        key: 'college',
        icon: Users,
        color: 'var(--text-muted)',
        who: `${college.code} members`,
        detail: `Everyone in ${college.name} · college:{${college.code}}`,
        level: 'View',
        tone: levelTone('View'),
      })
      if (dept && selected.kind !== 'College') {
        const short = dept.shortName ?? dept.name
        out.push({
          key: 'dept',
          icon: Users,
          color: 'var(--text-muted)',
          who: `${short} members`,
          detail: `Everyone in the department · dept:{${dept.code}}`,
          level: 'View',
          tone: levelTone('View'),
        })
      }
      if (program)
        out.push({
          key: 'program',
          icon: Users,
          color: 'var(--text-muted)',
          who: `${program.code} faculty`,
          detail: `Everyone in ${program.name} · prog:{${program.code}}`,
          level: 'View',
          tone: levelTone('View'),
        })
    } else {
      out.push({
        key: 'none',
        icon: FolderTree,
        color: 'var(--text-muted)',
        who: 'Nobody',
        detail: 'The root is shown for navigation only; access starts at each college and department',
        level: 'None',
        tone: levelTone('None'),
      })
    }
    out.push({
      key: 'admins',
      icon: Shield,
      color: 'var(--text-muted)',
      who: 'System Administrators',
      detail: 'Can repair access and re-run the org sync, but get no View on content',
      level: 'Repair only',
      tone: 'neutral',
      you: isAdmin,
    })
    return out
  }

  const createdBy =
    selected.kind === 'AcademicYear' || selected.kind === 'Term'
      ? 'On the first document filed for this period'
      : selected.kind === 'Course'
        ? 'On the first per-course document filed for this course'
      : selected.kind === 'AcademicRoot' || selected.kind === 'OfficeRoot'
        ? 'System setup'
        : 'Org sync'

  return (
    <>
      <PageHeader
        breadcrumb={
          <span className={styles.crumbs}>
            <Link to="/library">Library</Link>
            <span className={styles.sep}>/</span>
            <span>Academic folders</span>
          </span>
        }
        title="Academic folders"
        actions={
          <>
            <label className={styles.search}>
              <Search size={14} aria-hidden />
              <input value={query} placeholder={`Search in ${scopeName}`} onChange={(e) => setQuery(e.target.value)} />
              {query && (
                <button
                  type="button"
                  aria-label="Clear search"
                  onClick={() => {
                    setQuery('')
                    setQ('')
                  }}
                >
                  <X size={12} />
                </button>
              )}
            </label>
            <FilterChip<string>
              label="Academic term"
              value={termFilter}
              onChange={filterByTerm}
              options={[
                { value: '', label: 'All terms' },
                ...terms.map((t) => ({
                  value: t.id,
                  label: `${t.shortName} ${t.academicYearName.replace(/^AY\s*/, '')}${t.status === 'Active' ? ' (current)' : ''}`,
                })),
              ]}
            />
            <Button onClick={() => setInfoOpen(true)}>Folder info</Button>
          </>
        }
      />

      {hiddenRequest && (
        <div className={styles.hiddenNotice}>
          <EyeOff size={14} aria-hidden />
          That folder isn&apos;t visible to {me.displayName}, so you&apos;re seeing your own folders instead.
        </div>
      )}

      <div className={styles.body}>
        {tree}

        <div className={styles.view}>
          <section className={styles.head}>
            <div className={styles.headPath}>
              {selectedPath.map((f, i) => (
                <span key={f.id}>
                  {i > 0 && <span className={styles.chev}>›</span>}
                  {f.id !== selected.id && (
                    <button type="button" onClick={() => select(f.id)}>
                      {f.name}
                    </button>
                  )}
                </span>
              ))}
            </div>
            <div className={styles.titleRow}>
              <FolderOpen size={20} className={styles.titleIcon} aria-hidden />
              <h2 className={styles.title}>{q ? `Search results in ${scopeName}` : selected.name}</h2>
              <Badge tone="info">System folder</Badge>
              {term && (
                <Badge tone="purple">
                  Term {term.code} · {year?.name ?? term.academicYearName}
                </Badge>
              )}
              {selected.kind === 'AcademicYear' && year && <Badge tone="purple">{year.name}</Badge>}
              {filterTerm && <Badge tone="warning">Showing {filterLabel} only</Badge>}
              <span className={styles.flex} />
              {person && (
                <span className={styles.owner}>
                  Owner: {person.name} · {ownerRole}
                </span>
              )}
            </div>
            <div className={styles.mapping}>
              <Route size={16} aria-hidden />
              <span>{mapping()}</span>
            </div>
          </section>

          <section className={styles.docs}>
            <div className={`${styles.docRow} ${styles.docHead}`}>
              <span>Document</span>
              <span>Document type</span>
              <span>Number</span>
              <span>Status</span>
            </div>
            {!q &&
              !filterTerm &&
              subfolders.map((f) => {
                const Icon = kindIcon[f.kind]
                return (
                  <button key={f.id} type="button" className={`${styles.docRow} ${styles.folderRow}`} onClick={() => select(f.id)}>
                    <span className={styles.docCell}>
                      <Icon size={16} style={{ color: iconColor(f, false) }} aria-hidden />
                      <span className={styles.docText}>
                        <strong>{f.name}</strong>
                        <span>
                          {kindLabel[f.kind]}
                          {f.accessible && !isAdmin ? ` · ${countIn(f.id)} documents` : ''}
                        </span>
                      </span>
                    </span>
                    <span className={styles.subtle}>—</span>
                    <span className={styles.subtle}>—</span>
                    <span>{chipFor(f)}</span>
                  </button>
                )
              })}
            {rowsError ? (
              <ErrorState error={rowsError} onRetry={q ? search.reload : contents.reload} compact />
            ) : rowsLoading ? (
              <Loading label={q ? 'Searching…' : 'Loading documents…'} compact />
            ) : (
              rows.map((d) => {
                const where =
                  q || (filterTerm && d.folder.id !== selected.id)
                    ? pathOf(d.folder.id)
                        .slice(-3)
                        .map((f) => f.name)
                        .join(' › ') || d.folder.name
                    : ''
                return (
                  <div key={d.id} className={`${styles.docRow} ${styles.docBody}`} onClick={() => navigate(`/documents/${d.id}`)}>
                    <span className={styles.docCell}>
                      <FileText size={16} className={styles.fileIcon} aria-hidden />
                      <span className={styles.docText}>
                        <strong>{d.title}</strong>
                        <span>
                          {d.currentVersion ? `${d.currentVersion.fileName} · v${d.currentVersion.number}` : 'No file yet'}
                          {where ? ` · ${where}` : ''}
                        </span>
                      </span>
                    </span>
                    <span>
                      <TypeChip label={`${d.type.name} · ${d.type.code}`} />
                    </span>
                    <span className={styles.number}>{d.documentNumber ?? '—'}</span>
                    <span>
                      <Badge tone={stateTone[d.state]}>{stateLabel[d.state]}</Badge>
                    </span>
                  </div>
                )
              })
            )}
            {!rowsLoading && !rowsError && rows.length === 0 && (q || filterTerm || subfolders.length === 0) && (
              <div className={styles.empty}>
                {q
                  ? `No documents in ${scopeName} match “${query.trim()}”.`
                  : isAdmin
                    ? 'Documents are hidden: admins can repair access but not view content.'
                    : pathOnlySelected
                      ? 'Nothing to list here for you.'
                      : filterTerm
                        ? `No documents tagged ${filterLabel} here or in the folders below.`
                        : 'No documents in this folder yet.'}
              </div>
            )}
            <div className={styles.docFooter}>
              <Info size={14} aria-hidden />
              <span>
                {q
                  ? `${hits?.total ?? 0} result${hits?.total === 1 ? '' : 's'} in ${scopeName}${hits && hits.total > rows.length ? ` (showing ${rows.length})` : ''}.`
                  : rowsLoading
                    ? 'Loading…'
                    : footer()}
              </span>
            </div>
          </section>

          <div className={styles.bottom}>
            <section className={styles.card}>
              <div className={styles.cardHead}>
                <div className={styles.cardTitleBlock}>
                  <h3 className={styles.cardTitle}>Who can open this folder</h3>
                  <p className={styles.cardDesc}>
                    Inherited from the org tree. Nobody set these by hand, and they follow the role, not the person.
                  </p>
                </div>
                {canManageAccess && (
                  <Link to={`/library/folders/${selected.id}/access`} className={styles.cardLink}>
                    Access details
                  </Link>
                )}
              </div>
              {accessRows().map((a) => {
                const Icon = a.icon
                return (
                  <div key={a.key} className={styles.accessRow}>
                    <Icon size={16} style={{ color: a.color }} aria-hidden />
                    <span className={styles.accessWho}>
                      <strong>
                        {a.who}
                        {a.you ? ' (you)' : ''}
                      </strong>
                      <span>{a.detail}</span>
                    </span>
                    <Badge tone={a.tone}>{a.level}</Badge>
                  </div>
                )
              })}
            </section>
            <section className={styles.rules}>
              <h3 className={styles.rulesTitle}>
                <FolderLock size={16} aria-hidden />
                System folder
              </h3>
              <ul>
                <li>Created by the org sync or on the first submission, never by hand</li>
                <li>Can&apos;t be renamed, moved or deleted (409 system_folder)</li>
                <li>Follows the SIS: a renamed program renames its folder</li>
                <li>No manual uploads or custom subfolders here</li>
                <li>Moving a document out isn&apos;t allowed while it belongs to a submission</li>
              </ul>
            </section>
          </div>
        </div>
      </div>

      {infoOpen && (
        <Modal
          title={selected.name}
          description={detail.data?.context ?? kindLabel[selected.kind]}
          width={500}
          onClose={() => setInfoOpen(false)}
          footer={
            <>
              <Button onClick={() => setInfoOpen(false)}>Close</Button>
              {canManageAccess && (
                <Button variant="primary" icon={Shield} onClick={() => navigate(`/library/folders/${selected.id}/access`)}>
                  Open access page
                </Button>
              )}
            </>
          }
        >
          <dl className={styles.info}>
            <dt>Path</dt>
            <dd>{selectedPath.map((f) => f.name).join(' › ')}</dd>
            <dt>Folder id</dt>
            <dd className="mono">{selected.id}</dd>
            <dt>Created by</dt>
            <dd>{createdBy}</dd>
            {person && (
              <>
                <dt>Owner</dt>
                <dd>
                  {person.name} · {ownerRole}
                </dd>
              </>
            )}
            <dt>Your access</dt>
            <dd>{isAdmin ? 'Repair only (administrator)' : selected.accessible ? selected.myLevel : 'Path only'}</dd>
            <dt>Documents</dt>
            <dd>{isAdmin ? 'Hidden for admins' : `${countIn(selected.id)} you can see in this folder and below`}</dd>
            <dt>Manual changes</dt>
            <dd>None: system folders can&apos;t be renamed, moved, deleted or uploaded to by hand</dd>
          </dl>
        </Modal>
      )}
    </>
  )
}

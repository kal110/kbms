// Folder access screen (frame 13): the local "unsaved changes" model on top of the access API, and
// the helpers the screen shares between library and system folders.
import type { LucideIcon } from 'lucide-react'
import { Building, Building2, GraduationCap, Landmark, User, Users } from 'lucide-react'
import { grantAccess, revokeAccess, setInheritance } from '../../api/access'
import { cached } from '../../api/cache'
import { getDepartment, getOrgTree, getProgram } from '../../api/org'
import { getAcademicYears, getActiveTerm } from '../../api/reference'
import type { AccessLevel, EffectiveAccessDto, GrantDto, PrincipalDto } from '../../api/types'
import type { BadgeTone } from '../../components/Badge'
import { longDate, todayIso } from '../../lib/format'

export type GrantLevel = Exclude<AccessLevel, 'None'>

export const accessLevels: { key: GrantLevel; label: string; description: string }[] = [
  { key: 'View', label: 'Can view', description: 'See it in lists and search; preview in the browser.' },
  { key: 'Download', label: 'Can download', description: '+ get the original file.' },
  { key: 'Contribute', label: 'Can contribute', description: '+ upload new documents and versions here.' },
  { key: 'Edit', label: 'Can edit', description: '+ change metadata, versions and archive any document here.' },
  { key: 'Manage', label: 'Can manage', description: '+ change access, move, delete (subject to retention).' },
]

const ranks: AccessLevel[] = ['None', 'View', 'Download', 'Contribute', 'Edit', 'Manage']
export const levelRank = (level: AccessLevel) => ranks.indexOf(level)
export const levelLabel = (level: AccessLevel) => accessLevels.find((l) => l.key === level)?.label ?? 'No access'

export const levelTone: Record<AccessLevel, BadgeTone> = {
  None: 'neutral',
  View: 'neutral',
  Download: 'success',
  Contribute: 'info',
  Edit: 'info',
  Manage: 'purple',
}

export const principalIcon = (p?: Pick<PrincipalDto, 'type'>): LucideIcon => {
  switch (p?.type) {
    case 'User':
      return User
    case 'Group':
      return Users
    case 'Program':
    case 'ProgramSupervisor':
      return GraduationCap
    case 'Department':
    case 'DepartmentHead':
      return Building
    case 'Vpaf':
      return Landmark
    default:
      return Building2
  }
}

export const principalKind = (p: Pick<PrincipalDto, 'type'>) => (p.type === 'User' ? 'Person' : p.type === 'Group' ? 'Group' : 'Org unit')

/** "Aug 31, 2026", "Expired Aug 31, 2026" or "—". */
export const expiryText = (expiresAt: string | null | undefined, expired = false) =>
  expiresAt ? `${expired ? 'Expired ' : ''}${longDate(expiresAt)}` : '—'

// ---- Expiry presets, from the academic calendar

export interface ExpiryOption {
  /** DateOnly the grant ends on, or '' for no expiry. */
  date: string
  label: string
}

const addDays = (iso: string, days: number) => {
  const d = new Date(`${iso}T00:00:00Z`)
  d.setUTCDate(d.getUTCDate() + days)
  return d.toISOString().slice(0, 10)
}

/** No expiry, 30 days, end of the active term and end of its academic year. */
export async function loadExpiryOptions(): Promise<ExpiryOption[]> {
  const today = todayIso()
  const options: ExpiryOption[] = [{ date: '', label: 'No expiry' }]
  const in30 = addDays(today, 30)
  options.push({ date: in30, label: `${longDate(in30)} (30 days)` })
  const [term, years] = await Promise.all([getActiveTerm().catch(() => null), getAcademicYears().catch(() => [])])
  if (term && term.endsOn > today) options.push({ date: term.endsOn, label: `${longDate(term.endsOn)} (end of ${term.shortName})` })
  const year = years.find((y) => y.id === term?.academicYearId)
  if (year && year.endsOn > today) options.push({ date: year.endsOn, label: `${longDate(year.endsOn)} (end of ${year.name})` })
  return options
}

/** A grant ends at the end of its last day, in the user's time zone. */
export const expiryToInstant = (date: string) => (date ? new Date(`${date}T23:59:59`).toISOString() : null)

// ---- Unsaved changes

export interface PendingGrant {
  principal: PrincipalDto
  level: GrantLevel
  expiresAt: string | null
  /** Copied from the parent when stopping inheritance. */
  copied?: boolean
}

export interface Pending {
  added: PendingGrant[]
  /** Grant ids. */
  removed: string[]
  /** Grant id → new level. */
  levels: Record<string, GrantLevel>
  /** Level changes made by copying the parent's grants (undone with the stop). */
  copiedLevels: string[]
  inheritance?: 'stop' | 'resume'
}

export const emptyPending = (): Pending => ({ added: [], removed: [], levels: {}, copiedLevels: [] })

export const countChanges = (p: Pending) => p.added.length + p.removed.length + Object.keys(p.levels).length + (p.inheritance ? 1 : 0)

interface Step {
  run: () => Promise<unknown>
  /** The pending state once this step is saved. */
  done: (p: Pending) => Pending
}

const withoutLevel = (p: Pending, grantId: string) => {
  const levels = { ...p.levels }
  delete levels[grantId]
  return { ...p, levels, copiedLevels: p.copiedLevels.filter((id) => id !== grantId) }
}

/**
 * Saves the changes one call at a time, in an order that never locks the editor out: additions and
 * level changes first, then stopping or resuming inheritance (the API keeps a non-admin editor's
 * Manage when stopping), then removals, the editor's own grant last. Stops at the first failure and
 * returns what's left.
 */
export async function savePending(folderId: string, pending: Pending, grants: GrantDto[], myKey: string) {
  const steps: Step[] = []
  for (const g of pending.added) {
    steps.push({
      run: () =>
        grantAccess('folders', folderId, {
          principalType: g.principal.type,
          principalId: g.principal.id,
          level: g.level,
          expiresAt: g.expiresAt,
        }),
      done: (p) => ({ ...p, added: p.added.filter((x) => x !== g) }),
    })
  }
  for (const [grantId, level] of Object.entries(pending.levels)) {
    const grant = grants.find((g) => g.id === grantId)
    if (!grant) continue
    steps.push({
      run: () =>
        grantAccess('folders', folderId, {
          principalType: grant.principal.type,
          principalId: grant.principal.id,
          level,
          expiresAt: grant.expiresAt,
        }),
      done: (p) => withoutLevel(p, grantId),
    })
  }
  if (pending.inheritance) {
    const inherit = pending.inheritance === 'resume'
    steps.push({ run: () => setInheritance('folders', folderId, inherit), done: (p) => ({ ...p, inheritance: undefined }) })
  }
  // Re-adding someone whose grant was removed replaces that grant (one grant per principal), so
  // there's nothing left to delete.
  const readded = new Set(pending.added.map((g) => g.principal.key))
  const removals = pending.removed
    .map((id) => grants.find((g) => g.id === id))
    .filter((g): g is GrantDto => Boolean(g))
    .sort((a, b) => Number(a.principal.key === myKey) - Number(b.principal.key === myKey))
  for (const grant of removals) {
    const done = (p: Pending) => ({ ...p, removed: p.removed.filter((id) => id !== grant.id) })
    steps.push({ run: () => (readded.has(grant.principal.key) ? Promise.resolve() : revokeAccess('folders', folderId, grant.id)), done })
  }

  let remaining = pending
  let saved = 0
  for (const step of steps) {
    try {
      await step.run()
    } catch (error) {
      return { saved, total: steps.length, remaining, error }
    }
    remaining = step.done(remaining)
    saved++
  }
  return { saved, total: steps.length, remaining, error: undefined as unknown }
}

// ---- Who has access: grants as they apply now, or after saving

export interface Applying {
  principal: PrincipalDto
  level: AccessLevel
  /** Where the grant is set: this folder or the ancestor it's inherited from. */
  source: string
  inherited: boolean
  expiresAt?: string | null
}

export const fromEffective = (e: EffectiveAccessDto, grants: GrantDto[] = []): Applying => ({
  principal: e.principal,
  level: e.level,
  source: e.source.name,
  inherited: e.inherited,
  expiresAt: e.inherited ? null : grants.find((g) => g.principal.key === e.principal.key)?.expiresAt,
})

// ---- Check someone's access: which people each principal stands for

/**
 * Members of each principal, mirroring how the API gives a signed-in user their principal keys
 * (CurrentUser): faculty and the AS are in their program, department and college; the dean in the
 * college; staff and the head in their department. Group members aren't available from the API, so
 * groups map to null ("unknown").
 */
export async function resolveMembers(principals: PrincipalDto[]): Promise<Map<string, Set<string> | null>> {
  const tree = await getOrgTree()
  const program = (id: string) => cached(`org-program:${id}`, () => getProgram(id))
  const department = (id: string) => cached(`org-department:${id}`, () => getDepartment(id))
  const allDepartments = [...tree.colleges.flatMap((c) => c.departments), ...tree.nonAcademicDepartments]
  const allPrograms = allDepartments.flatMap((d) => d.programs)

  const programMembers = async (programId: string) => {
    const detail = await program(programId)
    const ids = new Set(detail.faculty.map((f) => f.person.id))
    if (detail.supervisor) ids.add(detail.supervisor.id)
    return ids
  }
  const union = (sets: Set<string>[]) => new Set(sets.flatMap((s) => [...s]))

  const result = new Map<string, Set<string> | null>()
  await Promise.all(
    principals.map(async (p) => {
      if (result.has(p.key)) return
      switch (p.type) {
        case 'User':
          result.set(p.key, new Set([p.id]))
          return
        case 'Group':
        case 'Vpaf': // The org tree doesn't list who holds the university-wide VPAF role.
          result.set(p.key, null)
          return
        case 'ProgramSupervisor': {
          const supervisor = allPrograms.find((x) => x.id === p.id)?.supervisor
          result.set(p.key, new Set(supervisor ? [supervisor.id] : []))
          return
        }
        case 'CollegeDean': {
          const dean = tree.colleges.find((c) => c.id === p.id)?.dean
          result.set(p.key, new Set(dean ? [dean.id] : []))
          return
        }
        case 'DepartmentHead': {
          const head = allDepartments.find((d) => d.id === p.id)?.head
          result.set(p.key, new Set(head ? [head.id] : []))
          return
        }
        case 'Program':
          result.set(p.key, await programMembers(p.id))
          return
        case 'Department': {
          const dept = allDepartments.find((d) => d.id === p.id)
          const detail = await department(p.id)
          const ids = union([
            new Set(detail.staff.map((s) => s.person.id)),
            ...(await Promise.all((dept?.programs ?? []).map((x) => programMembers(x.id)))),
          ])
          if (detail.head) ids.add(detail.head.id)
          result.set(p.key, ids)
          return
        }
        case 'College': {
          const college = tree.colleges.find((c) => c.id === p.id)
          const programs = (college?.departments ?? []).flatMap((d) => d.programs)
          const ids = union(await Promise.all(programs.map((x) => programMembers(x.id))))
          if (college?.dean) ids.add(college.dean.id)
          result.set(p.key, ids)
          return
        }
      }
    }),
  )
  return result
}

/** The rows that give `userId` access, and the highest level among them. */
export function checkAccess(rows: Applying[], members: Map<string, Set<string> | null> | undefined, userId: string) {
  const hits = rows.filter((r) => members?.get(r.principal.key)?.has(userId))
  const top = hits.reduce<AccessLevel | null>((acc, r) => (!acc || levelRank(r.level) > levelRank(acc) ? r.level : acc), null)
  const unknownGroups = rows.filter((r) => members?.get(r.principal.key) === null)
  return { hits, top, unknownGroups }
}

/** What the highest level still doesn't allow. */
export function cannotText(top: AccessLevel | null) {
  const r = top ? levelRank(top) : 0
  if (r <= 0) return 'Cannot see this folder or anything in it'
  if (r < 3) return 'Cannot upload, edit or change access here'
  if (r < 4) return 'Cannot edit or change access here'
  if (r < 5) return 'Cannot change access here'
  return null
}

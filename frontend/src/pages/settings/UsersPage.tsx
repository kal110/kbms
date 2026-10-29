import { useEffect, useMemo, useState } from 'react'
import { ChevronLeft, ChevronRight, Plus, Search, Star, UserMinus, X } from 'lucide-react'
import { ApiError } from '../../api/client'
import {
  addAdministrator,
  addDepartmentStaff,
  addFaculty,
  assignCollegeDean,
  assignDepartmentHead,
  assignProgramSupervisor,
  assignVpaf,
  createUser,
  getOrgTree,
  getUserForAdmin,
  listUsers,
  removeAdministrator,
  removeCollegeDean,
  removeDepartmentHead,
  removeDepartmentStaff,
  removeFaculty,
  removeProgramSupervisor,
  removeVpaf,
  setPrimaryProgram,
  updateUser,
} from '../../api/org'
import type { OrgCollegeDto, OrgDepartmentDto, OrgProgramDto, OrgTreeDto, UserAdminDto, UserRoleFilter } from '../../api/types'
import { useAction, useApi } from '../../api/useApi'
import { useMe } from '../../auth/SessionContext'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Avatar } from '../../components/Avatar'
import { Badge } from '../../components/Badge'
import type { BadgeTone } from '../../components/Badge'
import { Button } from '../../components/Button'
import { FilterChip } from '../../components/FilterChip'
import { Modal } from '../../components/Modal'
import { SettingsFrame } from './SettingsFrame'
import { UserPasswordSection } from './UserPasswordSection'
import panel from './panel.module.css'
import styles from './UsersPage.module.css'

// Settings → Users & roles: everyone who can sign in, what they hold (System Administrator, VPAF,
// Dean, Academic Supervisor, office Head) and where they belong (program faculty, office staff).
// Folder access follows these roles, so changing them here moves access without touching grants.

type RoleFilter = 'all' | NonNullable<UserRoleFilter>
type StatusFilter = 'active' | 'inactive' | 'all'
type RoleKind = 'faculty' | 'supervisor' | 'dean' | 'staff' | 'head' | 'vpaf' | 'admin'

const PAGE_SIZE = 50

const roleFilterOptions: { value: RoleFilter; label: string }[] = [
  { value: 'all', label: 'Any' },
  { value: 'Admin', label: 'System administrators' },
  { value: 'Vpaf', label: 'VPAF' },
  { value: 'Dean', label: 'Deans' },
  { value: 'Supervisor', label: 'Academic Supervisors' },
  { value: 'Head', label: 'Office heads' },
  { value: 'Faculty', label: 'Faculty' },
  { value: 'Staff', label: 'Office staff' },
  { value: 'None', label: 'No role yet' },
]

const roleKinds: { value: RoleKind; label: string }[] = [
  { value: 'faculty', label: 'Faculty of a program' },
  { value: 'supervisor', label: 'Academic Supervisor of a program' },
  { value: 'dean', label: 'Dean of a college' },
  { value: 'staff', label: 'Staff of a non-academic office' },
  { value: 'head', label: 'Head/Director of a non-academic office' },
  { value: 'vpaf', label: 'VP for Academic Affairs (VPAF)' },
  { value: 'admin', label: 'System Administrator' },
]

const roleKindHelp: Record<RoleKind, string> = {
  faculty: 'Submits for the program and gets a faculty folder there.',
  supervisor: 'Reviews and verifies the program’s submissions. One per program.',
  dean: 'Reviews dean-stage submissions and sees the college dashboard. One per college.',
  staff: 'Submits the office’s reports and gets a staff folder there.',
  head: 'Reviews the office’s submissions. One per office.',
  vpaf: 'Sets every required-documents list and sees every college’s submissions, read-only. One for the university.',
  admin: 'Manages settings, people, processing and audit. Doesn’t see documents unless they’re shared.',
}

// ---- org units

interface ProgramChoice {
  program: OrgProgramDto
  college: OrgCollegeDto
}

interface Units {
  colleges: OrgCollegeDto[]
  programs: ProgramChoice[]
  offices: OrgDepartmentDto[]
}

function unitsOf(tree: OrgTreeDto | undefined): Units {
  if (!tree) return { colleges: [], programs: [], offices: [] }
  return {
    colleges: tree.colleges,
    programs: tree.colleges.flatMap((college) =>
      college.departments.flatMap((d) => d.programs.map((program) => ({ program, college }))),
    ),
    offices: tree.nonAcademicDepartments,
  }
}

/** "Dean of COE · AS of BSIT, BSEMC · Head of LIB": positions nobody holds. */
function vacancies(units: Units) {
  const parts = [
    ['Dean of', units.colleges.filter((c) => !c.dean).map((c) => c.code)],
    ['Academic Supervisor of', units.programs.filter((p) => !p.program.supervisor).map((p) => p.program.code)],
    ['Head of', units.offices.filter((o) => !o.head).map((o) => o.code)],
  ] as const
  return parts.filter(([, codes]) => codes.length).map(([what, codes]) => `${what} ${codes.join(', ')}`)
}

// ---- what a user holds

interface Chip {
  key: string
  label: string
  tone: BadgeTone
}

function chipsOf(user: UserAdminDto): Chip[] {
  const roles = user.roles.map((r): Chip => {
    const code = r.scope?.code ?? ''
    switch (r.role) {
      case 'SystemAdmin':
        return { key: 'admin', label: 'Admin', tone: 'purple' }
      case 'Vpaf':
        return { key: 'vpaf', label: 'VPAF', tone: 'purple' }
      case 'Dean':
        return { key: `dean-${code}`, label: `Dean · ${code}`, tone: 'info' }
      case 'AcademicSupervisor':
        return { key: `as-${code}`, label: `AS · ${code}`, tone: 'info' }
      default:
        return { key: `head-${code}`, label: `${r.title ?? 'Head'} · ${code}`, tone: 'info' }
    }
  })
  return [
    ...roles,
    ...user.programs.map((p) => ({ key: `fac-${p.program.id}`, label: `Faculty · ${p.program.code}`, tone: 'neutral' as const })),
    ...user.departments.map((d) => ({ key: `staff-${d.department.id}`, label: `Staff · ${d.department.code}`, tone: 'neutral' as const })),
  ]
}

interface Holding {
  key: string
  label: string
  unit?: string
  note?: string
  /** "Remove … as Dean of CCS?" */
  question: string
  consequence: string
  /** Why it can't be removed here. */
  blocked?: string
  remove: () => Promise<unknown>
  makePrimary?: () => Promise<UserAdminDto>
}

function holdingsOf(user: UserAdminDto, meId: string): Holding[] {
  const who = user.displayName
  const roles = user.roles.map((r): Holding => {
    const scope = r.scope
    const unit = scope ? `${scope.code} · ${scope.name}` : undefined
    switch (r.role) {
      case 'SystemAdmin':
        return {
          key: 'admin',
          label: 'System Administrator',
          question: `Remove ${who} as a System Administrator?`,
          consequence: 'They lose Settings, Users & roles, Processing and Audit.',
          blocked: user.id === meId ? 'You can’t remove your own administrator role.' : undefined,
          remove: () => removeAdministrator(user.id),
        }
      case 'Vpaf':
        return {
          key: 'vpaf',
          label: r.title ?? 'VP for Academic Affairs',
          unit: 'University-wide',
          question: `Remove ${who} as VPAF?`,
          consequence: 'Until you assign a new VPAF, only administrators can change required-documents lists.',
          remove: () => removeVpaf(),
        }
      case 'Dean':
        return {
          key: `dean-${scope?.id}`,
          label: 'College Dean',
          unit,
          question: `Remove ${who} as Dean of ${scope?.code}?`,
          consequence: `${scope?.code} has no Dean until you assign one; Dean-stage reviews fall back to a system administrator.`,
          remove: () => removeCollegeDean(scope!.id),
        }
      case 'AcademicSupervisor':
        return {
          key: `as-${scope?.id}`,
          label: 'Academic Supervisor',
          unit,
          question: `Remove ${who} as Academic Supervisor of ${scope?.code}?`,
          consequence: `${scope?.code} reviews go to the College Dean until you assign a new Academic Supervisor.`,
          remove: () => removeProgramSupervisor(scope!.id),
        }
      default:
        return {
          key: `head-${scope?.id}`,
          label: r.title ?? 'Head',
          unit,
          question: `Remove ${who} as ${r.title ?? 'Head'} of ${scope?.code}?`,
          consequence: `${scope?.code} submissions go to a system administrator until you assign a new head.`,
          remove: () => removeDepartmentHead(scope!.id),
        }
    }
  })

  const faculty = user.programs.map(
    (p): Holding => ({
      key: `fac-${p.program.id}`,
      label: 'Faculty',
      unit: `${p.program.code} · ${p.program.name}`,
      note: p.isPrimary ? `${p.college.code} · primary program` : p.college.code,
      question: `Remove ${who} from the ${p.program.code} faculty?`,
      consequence: `Their folder and documents stay; they stop submitting for ${p.program.code}.`,
      remove: () => removeFaculty(p.program.id, user.id),
      makePrimary: user.programs.length > 1 && !p.isPrimary ? () => setPrimaryProgram(p.program.id, user.id) : undefined,
    }),
  )

  const staff = user.departments.map(
    (d): Holding => ({
      key: `staff-${d.department.id}`,
      label: 'Office staff',
      unit: `${d.department.code} · ${d.department.name}`,
      note: d.position ?? undefined,
      question: `Remove ${who} from the ${d.department.code} staff?`,
      consequence: `Their folder and documents stay; they stop submitting for ${d.department.code}.`,
      remove: () => removeDepartmentStaff(d.department.id, user.id),
    }),
  )

  return [...roles, ...faculty, ...staff]
}

const fieldError = (error: unknown, field: string) =>
  error instanceof ApiError ? error.errors?.[field]?.join(' ') : undefined

/** An action error not already shown next to a field. */
const formError = (error: unknown) =>
  error instanceof ApiError && error.errors && Object.keys(error.errors).length ? undefined : error

const emailFormat = /^[^@\s]+@[^@\s]+\.[^@\s]+$/

// ---- page

export function UsersPage() {
  const me = useMe()
  const [query, setQuery] = useState('')
  const [search, setSearch] = useState('')
  const [roleFilter, setRoleFilter] = useState<RoleFilter>('all')
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('active')
  const [page, setPage] = useState(1)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [latest, setLatest] = useState<UserAdminDto | null>(null)
  const [creating, setCreating] = useState(false)

  // Search as you type, without a request per keystroke.
  useEffect(() => {
    const id = window.setTimeout(() => {
      setSearch(query.trim())
      setPage(1)
    }, 250)
    return () => window.clearTimeout(id)
  }, [query])

  const users = useApi(
    (signal) =>
      listUsers(
        {
          q: search,
          role: roleFilter === 'all' ? undefined : roleFilter,
          active: statusFilter === 'all' ? undefined : statusFilter === 'active',
          page,
          pageSize: PAGE_SIZE,
        },
        signal,
      ),
    [search, roleFilter, statusFilter, page],
  )
  const tree = useApi(() => getOrgTree(), [])
  const units = useMemo(() => unitsOf(tree.data), [tree.data])
  const open = useMemo(() => vacancies(units), [units])

  const rows = users.data?.items ?? []
  // The last saved or created person stays open even when the filters hide them.
  const current =
    (selectedId && (rows.find((u) => u.id === selectedId) ?? (latest?.id === selectedId ? latest : null))) || rows[0] || null
  const total = users.data?.total ?? 0
  const from = total ? (page - 1) * PAGE_SIZE + 1 : 0
  const to = Math.min(page * PAGE_SIZE, total)

  const changed = (next: UserAdminDto) => {
    users.setData((d) => d && { ...d, items: d.items.map((u) => (u.id === next.id ? next : u)) })
    setLatest(next)
    setSelectedId(next.id)
    users.reload()
    tree.reload()
  }

  const created = (user: UserAdminDto) => {
    setCreating(false)
    changed(user)
  }

  const filter = <T,>(set: (value: T) => void) => (value: T) => {
    set(value)
    setPage(1)
  }

  return (
    <SettingsFrame
      actions={
        <Button variant="primary" icon={Plus} onClick={() => setCreating(true)}>
          Add person
        </Button>
      }
    >
      <div className={panel.body}>
        <section className={`${panel.card} ${panel.grow}`} aria-label="People">
          <div className={styles.toolbar}>
            <label className={styles.search}>
              <Search size={14} aria-hidden />
              <input
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder="Search name, email or sign-in id…"
                aria-label="Search people"
              />
            </label>
            <FilterChip<RoleFilter> label="Role" value={roleFilter} onChange={filter(setRoleFilter)} options={roleFilterOptions} />
            <FilterChip<StatusFilter>
              label="Status"
              value={statusFilter}
              onChange={filter(setStatusFilter)}
              options={[
                { value: 'active', label: 'Active' },
                { value: 'inactive', label: 'Deactivated' },
                { value: 'all', label: 'All' },
              ]}
            />
            <span className={styles.counter}>{users.data ? `${total.toLocaleString('en-US')} ${total === 1 ? 'person' : 'people'}` : ''}</span>
          </div>

          <div className={`${panel.headRow} ${styles.head}`}>
            <span className={`${panel.th} ${panel.fill}`}>Person</span>
            <span className={`${panel.th} ${styles.colRoles}`}>Roles & memberships</span>
            <span className={`${panel.th} ${styles.colStatus}`}>Status</span>
          </div>

          <div className={`${styles.rows} ${users.loading && users.data ? styles.stale : ''}`}>
            {!users.data && !users.error && <Loading compact label="Loading people…" />}
            {users.error ? <ErrorState compact error={users.error} onRetry={users.reload} /> : null}
            {rows.map((u) => {
              const chips = chipsOf(u)
              return (
                <button
                  key={u.id}
                  type="button"
                  className={`${styles.row} ${u.id === current?.id ? styles.selected : ''} ${u.active ? '' : styles.inactive}`}
                  aria-pressed={u.id === current?.id}
                  onClick={() => setSelectedId(u.id)}
                >
                  <span className={`${styles.person} ${panel.fill}`}>
                    <Avatar initials={u.initials} size={30} color={u.active ? undefined : 'var(--text-subtle)'} />
                    <span className={styles.personText}>
                      <span className={styles.personName}>
                        {u.displayName}
                        {u.id === me.id && <span className={styles.you}> (you)</span>}
                      </span>
                      <span className={styles.personMeta}>{u.email ?? u.externalId}</span>
                    </span>
                  </span>
                  <span className={`${panel.cell} ${styles.colRoles}`}>
                    {chips.length ? (
                      <span className={styles.chips}>
                        {chips.map((c) => (
                          <Badge key={c.key} tone={c.tone}>
                            {c.label}
                          </Badge>
                        ))}
                      </span>
                    ) : (
                      <span className={panel.muted}>No role yet</span>
                    )}
                  </span>
                  <span className={`${panel.cell} ${styles.colStatus}`}>
                    <Badge tone={u.active ? 'success' : 'neutral'}>{u.active ? 'Active' : 'Deactivated'}</Badge>
                  </span>
                </button>
              )
            })}
            {users.data && rows.length === 0 && (
              <div className={panel.empty}>{search || roleFilter !== 'all' ? 'Nobody matches these filters.' : 'No people yet.'}</div>
            )}
          </div>

          {total > PAGE_SIZE && (
            <div className={styles.pager}>
              <span>
                {from}–{to} of {total.toLocaleString('en-US')}
              </span>
              <Button size="sm" icon={ChevronLeft} disabled={page === 1} onClick={() => setPage((p) => p - 1)} aria-label="Previous page" />
              <Button size="sm" icon={ChevronRight} disabled={to >= total} onClick={() => setPage((p) => p + 1)} aria-label="Next page" />
            </div>
          )}

          <div className={styles.footer}>
            {tree.data
              ? open.length
                ? `Vacant: ${open.join(' · ')}`
                : 'Every college has a Dean, every program an Academic Supervisor and every office a head.'
              : 'Deans, Academic Supervisors and office heads: one per unit.'}
          </div>
        </section>

        {current && <UserDrawer key={current.id} user={current} units={units} meId={me.id} onChanged={changed} />}
      </div>

      {creating && <NewUserModal onCreate={created} onClose={() => setCreating(false)} />}
    </SettingsFrame>
  )
}

// ---- drawer

interface Profile {
  displayName: string
  email: string
  title: string
}

const toProfile = (u: UserAdminDto): Profile => ({ displayName: u.displayName, email: u.email ?? '', title: u.title ?? '' })

interface UserDrawerProps {
  user: UserAdminDto
  units: Units
  meId: string
  onChanged: (user: UserAdminDto) => void
}

function UserDrawer({ user, units, meId, onChanged }: UserDrawerProps) {
  const [profile, setProfile] = useState(() => toProfile(user))
  const [saved, setSaved] = useState(false)
  const [removing, setRemoving] = useState<Holding | null>(null)
  const [deactivating, setDeactivating] = useState(false)
  const save = useAction(updateUser)
  const reactivate = useAction(() => updateUser(user.id, { displayName: null, email: null, title: null, active: true }))
  const makePrimary = useAction((h: Holding) => h.makePrimary!())

  const before = toProfile(user)
  const name = profile.displayName.trim()
  const email = profile.email.trim()
  const title = profile.title.trim()
  const dirty = name !== before.displayName || email !== before.email || title !== before.title
  const problems = {
    displayName: !name ? 'A person needs a name.' : null,
    email: email && !emailFormat.test(email) ? 'Enter an email address like name@usa.edu.ph.' : null,
  }
  const valid = !problems.displayName && !problems.email
  const holdings = holdingsOf(user, meId)
  const set = <K extends keyof Profile>(key: K, value: string) => {
    setSaved(false)
    setProfile((p) => ({ ...p, [key]: value }))
  }

  const submit = async () => {
    if (!dirty || !valid) return
    const next = await save.run(user.id, {
      displayName: name !== before.displayName ? name : null,
      email: email !== before.email ? email : null,
      title: title !== before.title ? title : null,
      active: null,
    })
    if (!next) return
    onChanged(next)
    setProfile(toProfile(next))
    setSaved(true)
  }

  const primary = async (h: Holding) => {
    const next = await makePrimary.run(h)
    if (next) onChanged(next)
  }

  const restore = async () => {
    const next = await reactivate.run()
    if (next) onChanged(next)
  }

  return (
    <aside className={panel.drawer} aria-label="Person">
      <div className={`${panel.drawerHead} ${styles.drawerHead}`}>
        <Avatar initials={user.initials} size={40} color={user.active ? undefined : 'var(--text-subtle)'} />
        <span className={styles.drawerHeadText}>
          <span className={panel.drawerTitle}>{user.displayName}</span>
          <span className={panel.drawerSub}>
            Sign-in id <code className={styles.externalId}>{user.externalId}</code>
          </span>
        </span>
        <Badge tone={user.active ? 'success' : 'neutral'}>{user.active ? 'Active' : 'Deactivated'}</Badge>
      </div>
      <div className={panel.rule} />

      <div className={`${panel.form} ${styles.drawerScroll}`}>
        <label className={panel.field}>
          <span className={panel.label}>Name</span>
          <input
            className={`${panel.input} ${problems.displayName || fieldError(save.error, 'displayName') ? panel.inputInvalid : ''}`}
            value={profile.displayName}
            maxLength={200}
            onChange={(e) => set('displayName', e.target.value)}
          />
          {(problems.displayName || fieldError(save.error, 'displayName')) && (
            <span className={panel.error}>{problems.displayName ?? fieldError(save.error, 'displayName')}</span>
          )}
        </label>
        <div className={panel.fieldRow}>
          <label className={`${panel.field} ${panel.fill}`}>
            <span className={panel.label}>Email</span>
            <input
              className={`${panel.input} ${problems.email || fieldError(save.error, 'email') ? panel.inputInvalid : ''}`}
              value={profile.email}
              type="email"
              maxLength={254}
              placeholder="name@usa.edu.ph"
              onChange={(e) => set('email', e.target.value)}
            />
          </label>
          <label className={`${panel.field} ${styles.titleField}`}>
            <span className={panel.label}>Title</span>
            <input
              className={panel.input}
              value={profile.title}
              maxLength={120}
              placeholder="e.g. Instructor"
              onChange={(e) => set('title', e.target.value)}
            />
          </label>
        </div>
        {(problems.email || fieldError(save.error, 'email')) && (
          <span className={panel.error}>{problems.email ?? fieldError(save.error, 'email')}</span>
        )}
        <div className={styles.profileActions}>
          {saved && !dirty && <span className={styles.saved}>Saved</span>}
          {dirty && (
            <Button size="sm" onClick={() => setProfile(before)} disabled={save.pending}>
              Discard
            </Button>
          )}
          <Button size="sm" variant="primary" disabled={!dirty || !valid || save.pending} onClick={() => void submit()}>
            {save.pending ? 'Saving…' : 'Save details'}
          </Button>
        </div>
        <InlineError error={formError(save.error)} />

        <UserPasswordSection user={user} meId={meId} onChanged={onChanged} />

        <div className={styles.section}>
          <span className={panel.eyebrow}>Roles & memberships</span>
          {holdings.length ? (
            <ul className={styles.holdings}>
              {holdings.map((h) => (
                <li key={h.key} className={styles.holding}>
                  <span className={styles.holdingText}>
                    <span className={styles.holdingLabel}>{h.label}</span>
                    {h.unit && <span className={styles.holdingUnit}>{h.unit}</span>}
                    {h.note && <span className={styles.holdingNote}>{h.note}</span>}
                  </span>
                  {h.makePrimary && (
                    <button
                      type="button"
                      className={styles.iconButton}
                      title="Make this their primary program"
                      aria-label={`Make ${h.unit} their primary program`}
                      disabled={makePrimary.pending}
                      onClick={() => void primary(h)}
                    >
                      <Star size={14} />
                    </button>
                  )}
                  <button
                    type="button"
                    className={`${styles.iconButton} ${styles.removeButton}`}
                    title={h.blocked ?? 'Remove'}
                    aria-label={`Remove ${h.label}${h.unit ? ` · ${h.unit}` : ''}`}
                    disabled={Boolean(h.blocked)}
                    onClick={() => setRemoving(h)}
                  >
                    <X size={14} />
                  </button>
                </li>
              ))}
            </ul>
          ) : (
            <span className={panel.help}>
              No role or membership yet. Add one below so they can submit, review or manage.
            </span>
          )}
          <InlineError error={makePrimary.error} />
        </div>

        {user.active ? (
          <AddRoleForm user={user} units={units} onDone={onChanged} />
        ) : (
          <p className={panel.help}>Reactivate the account to give it roles.</p>
        )}
      </div>

      {reactivate.error ? (
        <div className={styles.actionError}>
          <InlineError error={reactivate.error} />
        </div>
      ) : null}
      <div className={panel.drawerFooter}>
        {user.active ? (
          <Button
            icon={UserMinus}
            onClick={() => setDeactivating(true)}
            disabled={user.id === meId}
            title={user.id === meId ? 'You can’t deactivate your own account' : undefined}
          >
            Deactivate…
          </Button>
        ) : (
          <Button variant="primary" onClick={() => void restore()} disabled={reactivate.pending}>
            {reactivate.pending ? 'Reactivating…' : 'Reactivate account'}
          </Button>
        )}
        <span className={styles.since}>Added {new Date(user.createdAt).toLocaleDateString('en-US', { dateStyle: 'medium' })}</span>
      </div>

      {removing && (
        <RemoveModal
          holding={removing}
          userId={user.id}
          onRemoved={(next) => {
            setRemoving(null)
            onChanged(next)
          }}
          onClose={() => setRemoving(null)}
        />
      )}
      {deactivating && (
        <DeactivateModal
          user={user}
          onDone={(next) => {
            setDeactivating(false)
            onChanged(next)
          }}
          onClose={() => setDeactivating(false)}
        />
      )}
    </aside>
  )
}

// ---- add a role

interface AddRoleFormProps {
  user: UserAdminDto
  units: Units
  onDone: (user: UserAdminDto) => void
}

function AddRoleForm({ user, units, onDone }: AddRoleFormProps) {
  const [kind, setKind] = useState<RoleKind | ''>('')
  const [unitId, setUnitId] = useState('')
  const [headTitle, setHeadTitle] = useState('Director')
  const [position, setPosition] = useState('')
  const [primary, setPrimary] = useState(false)
  // Only to say whose VPAF role this takes over.
  const vpaf = useApi(
    (signal) => (kind === 'vpaf' ? listUsers({ role: 'Vpaf', pageSize: 1 }, signal) : Promise.resolve(null)),
    [kind],
  )

  const holds = (role: string, scopeId?: string) => user.roles.some((r) => r.role === role && (!scopeId || r.scope?.id === scopeId))
  const kinds = roleKinds.filter((k) => !(k.value === 'admin' && holds('SystemAdmin')) && !(k.value === 'vpaf' && holds('Vpaf')))

  const programChoices = units.programs.filter(({ program }) =>
    kind === 'faculty' ? !user.programs.some((p) => p.program.id === program.id) : !holds('AcademicSupervisor', program.id),
  )
  const collegeChoices = units.colleges.filter((c) => !holds('Dean', c.id))
  const officeChoices = units.offices.filter((o) =>
    kind === 'staff' ? !user.departments.some((d) => d.department.id === o.id) : !holds('DepartmentHead', o.id),
  )
  const needsUnit = kind !== '' && kind !== 'admin' && kind !== 'vpaf'

  const replaces = (() => {
    switch (kind) {
      case 'dean': {
        const c = units.colleges.find((x) => x.id === unitId)
        return c?.dean && c.dean.id !== user.id ? `${c.dean.displayName} is Dean of ${c.code} now; this replaces them.` : null
      }
      case 'supervisor': {
        const p = units.programs.find((x) => x.program.id === unitId)?.program
        return p?.supervisor && p.supervisor.id !== user.id
          ? `${p.supervisor.displayName} is Academic Supervisor of ${p.code} now; this replaces them.`
          : null
      }
      case 'head': {
        const o = units.offices.find((x) => x.id === unitId)
        return o?.head && o.head.id !== user.id ? `${o.head.displayName} is ${o.headTitle ?? 'Head'} of ${o.code} now; this replaces them.` : null
      }
      case 'vpaf': {
        const holder = vpaf.data?.items[0]
        return holder && holder.id !== user.id ? `${holder.displayName} is the VPAF now; this replaces them.` : null
      }
      default:
        return null
    }
  })()

  const assign = useAction(async (): Promise<UserAdminDto> => {
    switch (kind) {
      case 'admin':
        return addAdministrator(user.id)
      case 'vpaf':
        return assignVpaf(user.id)
      case 'faculty':
        return addFaculty(unitId, { userId: user.id, isPrimary: primary })
      case 'dean':
        await assignCollegeDean(unitId, { userId: user.id })
        break
      case 'supervisor':
        await assignProgramSupervisor(unitId, { userId: user.id })
        break
      case 'head':
        await assignDepartmentHead(unitId, { userId: user.id, title: headTitle.trim() || null })
        break
      case 'staff':
        await addDepartmentStaff(unitId, { userId: user.id, position: position.trim() || null })
        break
    }
    return getUserForAdmin(user.id)
  })

  const choose = (next: RoleKind | '') => {
    setKind(next)
    setUnitId('')
    assign.clearError()
  }

  const submit = async () => {
    const next = await assign.run()
    if (!next) return
    choose('')
    onDone(next)
  }

  const programOptions = (
    <>
      <option value="">Choose a program…</option>
      {units.colleges.map((college) => {
        const choices = programChoices.filter((p) => p.college.id === college.id)
        return choices.length ? (
          <optgroup key={college.id} label={`${college.code} · ${college.name}`}>
            {choices.map(({ program }) => (
              <option key={program.id} value={program.id}>
                {program.code} · {program.name}
              </option>
            ))}
          </optgroup>
        ) : null
      })}
    </>
  )

  return (
    <div className={`${panel.box} ${styles.addRole}`}>
      <span className={panel.boxLabel}>Add a role</span>
      <select className={panel.select} value={kind} onChange={(e) => choose(e.target.value as RoleKind | '')} aria-label="Role">
        <option value="">Choose a role…</option>
        {kinds.map((k) => (
          <option key={k.value} value={k.value}>
            {k.label}
          </option>
        ))}
      </select>
      {kind && <span className={panel.help}>{roleKindHelp[kind]}</span>}

      {(kind === 'faculty' || kind === 'supervisor') && (
        <select className={panel.select} value={unitId} onChange={(e) => setUnitId(e.target.value)} aria-label="Program">
          {programOptions}
        </select>
      )}
      {kind === 'dean' && (
        <select className={panel.select} value={unitId} onChange={(e) => setUnitId(e.target.value)} aria-label="College">
          <option value="">Choose a college…</option>
          {collegeChoices.map((c) => (
            <option key={c.id} value={c.id}>
              {c.code} · {c.name}
            </option>
          ))}
        </select>
      )}
      {(kind === 'staff' || kind === 'head') && (
        <select className={panel.select} value={unitId} onChange={(e) => setUnitId(e.target.value)} aria-label="Office">
          <option value="">Choose an office…</option>
          {officeChoices.map((o) => (
            <option key={o.id} value={o.id}>
              {o.code} · {o.name}
            </option>
          ))}
        </select>
      )}
      {kind === 'head' && (
        <label className={panel.field}>
          <span className={panel.label}>Title</span>
          <input className={panel.input} value={headTitle} maxLength={60} onChange={(e) => setHeadTitle(e.target.value)} />
        </label>
      )}
      {kind === 'staff' && (
        <label className={panel.field}>
          <span className={panel.label}>Position (optional)</span>
          <input
            className={panel.input}
            value={position}
            maxLength={120}
            placeholder="e.g. Network administrator"
            onChange={(e) => setPosition(e.target.value)}
          />
        </label>
      )}
      {kind === 'faculty' && user.programs.length > 0 && (
        <label className={styles.check}>
          <input type="checkbox" checked={primary} onChange={(e) => setPrimary(e.target.checked)} />
          Make it their primary program (now {user.programs.find((p) => p.isPrimary)?.program.code ?? 'none'})
        </label>
      )}
      {replaces && <span className={panel.warn}>{replaces}</span>}
      <InlineError error={assign.error} />
      {kind && (
        <div className={styles.addActions}>
          <Button size="sm" onClick={() => choose('')}>
            Cancel
          </Button>
          <Button size="sm" variant="primary" disabled={(needsUnit && !unitId) || assign.pending} onClick={() => void submit()}>
            {assign.pending ? 'Assigning…' : replaces ? 'Replace' : 'Assign'}
          </Button>
        </div>
      )}
    </div>
  )
}

// ---- modals

interface RemoveModalProps {
  holding: Holding
  userId: string
  onRemoved: (user: UserAdminDto) => void
  onClose: () => void
}

function RemoveModal({ holding, userId, onRemoved, onClose }: RemoveModalProps) {
  const remove = useAction(async () => {
    await holding.remove()
    return getUserForAdmin(userId)
  })

  const confirm = async () => {
    const next = await remove.run()
    if (next) onRemoved(next)
  }

  return (
    <Modal
      title={holding.question}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="danger" onClick={() => void confirm()} disabled={remove.pending}>
            {remove.pending ? 'Removing…' : 'Remove'}
          </Button>
        </>
      }
    >
      <p className={panel.help}>{holding.consequence}</p>
      <InlineError error={remove.error} />
    </Modal>
  )
}

interface DeactivateModalProps {
  user: UserAdminDto
  onDone: (user: UserAdminDto) => void
  onClose: () => void
}

function DeactivateModal({ user, onDone, onClose }: DeactivateModalProps) {
  const deactivate = useAction(() => updateUser(user.id, { displayName: null, email: null, title: null, active: false }))
  const holdsRole = user.roles.length > 0

  const confirm = async () => {
    const next = await deactivate.run()
    if (next) onDone(next)
  }

  return (
    <Modal
      title={`Deactivate ${user.displayName}?`}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="danger" onClick={() => void confirm()} disabled={deactivate.pending || holdsRole}>
            {deactivate.pending ? 'Deactivating…' : 'Deactivate'}
          </Button>
        </>
      }
    >
      <p className={panel.help}>
        They can’t sign in any more. Their folders, documents and submissions stay, and you can reactivate the account
        later.
      </p>
      {holdsRole && (
        <p className={panel.warn}>
          Hand over their roles first ({chipsOf({ ...user, programs: [], departments: [] }).map((c) => c.label).join(', ')}):
          assign someone else or remove the role, so no unit is left with a reviewer who can’t sign in.
        </p>
      )}
      <InlineError error={deactivate.error} />
    </Modal>
  )
}

function NewUserModal({ onCreate, onClose }: { onCreate: (user: UserAdminDto) => void; onClose: () => void }) {
  const [displayName, setDisplayName] = useState('')
  const [externalId, setExternalId] = useState('')
  const [email, setEmail] = useState('')
  const [title, setTitle] = useState('')
  const [submitted, setSubmitted] = useState(false)
  const create = useAction(createUser)

  const errors = {
    displayName: !displayName.trim() ? 'Enter their name.' : null,
    externalId: !externalId.trim()
      ? 'Enter the id they sign in with.'
      : /\s/.test(externalId.trim())
        ? 'A sign-in id has no spaces.'
        : null,
    email: email.trim() && !emailFormat.test(email.trim()) ? 'Enter an email address like name@usa.edu.ph.' : null,
  }
  const valid = !errors.displayName && !errors.externalId && !errors.email
  const taken = create.error instanceof ApiError && create.error.code === 'external-id-taken'

  const submit = async () => {
    setSubmitted(true)
    if (!valid) return
    const user = await create.run({
      externalId: externalId.trim(),
      displayName: displayName.trim(),
      email: email.trim() || null,
      title: title.trim() || null,
    })
    if (user) onCreate(user)
  }

  const showError = (key: keyof typeof errors) => (submitted && errors[key]) || fieldError(create.error, key) || null

  return (
    <Modal
      title="Add a person"
      description="People usually arrive from the university sign-in. Add someone here to give them roles before their first sign-in."
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" onClick={() => void submit()} disabled={(submitted && !valid) || create.pending}>
            {create.pending ? 'Adding…' : 'Add person'}
          </Button>
        </>
      }
    >
      <label className={panel.field}>
        <span className={panel.label}>Name</span>
        <input
          className={`${panel.input} ${showError('displayName') ? panel.inputInvalid : ''}`}
          value={displayName}
          autoFocus
          maxLength={200}
          placeholder="e.g. Prof. Maria Santos"
          onChange={(e) => setDisplayName(e.target.value)}
        />
        {showError('displayName') && <span className={panel.error}>{showError('displayName')}</span>}
      </label>
      <label className={panel.field}>
        <span className={panel.label}>Sign-in id</span>
        <input
          className={`${panel.input} ${showError('externalId') || taken ? panel.inputInvalid : ''}`}
          value={externalId}
          maxLength={200}
          placeholder="e.g. maria.santos"
          onChange={(e) => setExternalId(e.target.value)}
        />
        {showError('externalId') ? (
          <span className={panel.error}>{showError('externalId')}</span>
        ) : (
          <span className={panel.help}>
            What they type to sign in, e.g. their email’s name part. It can’t change later. Set their password
            after adding them.
          </span>
        )}
      </label>
      <div className={panel.fieldRow}>
        <label className={`${panel.field} ${panel.fill}`}>
          <span className={panel.label}>Email (optional)</span>
          <input
            className={`${panel.input} ${showError('email') ? panel.inputInvalid : ''}`}
            value={email}
            type="email"
            maxLength={254}
            placeholder="name@usa.edu.ph"
            onChange={(e) => setEmail(e.target.value)}
          />
          {showError('email') && <span className={panel.error}>{showError('email')}</span>}
        </label>
        <label className={`${panel.field} ${panel.fill}`}>
          <span className={panel.label}>Title (optional)</span>
          <input className={panel.input} value={title} maxLength={120} placeholder="e.g. Instructor" onChange={(e) => setTitle(e.target.value)} />
        </label>
      </div>
      <InlineError error={formError(create.error)} />
    </Modal>
  )
}

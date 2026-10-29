import { useEffect, useRef, useState } from 'react'
import {
  Calendar,
  ChevronDown,
  CornerLeftUp,
  FolderLock,
  FolderOpen,
  GitFork,
  Shield,
  TriangleAlert,
  Undo2,
  UserPlus,
  UserSearch,
  X,
} from 'lucide-react'
import { Link, Navigate, useNavigate, useParams } from 'react-router'
import { getAccess, revokeAccess, searchPrincipals } from '../../api/access'
import { listAuditEvents } from '../../api/admin'
import { ApiError } from '../../api/client'
import { getFolder, getFolderTree } from '../../api/library'
import { searchPeople } from '../../api/org'
import type { AccessLevel, AuditRow, FolderDetail, FolderNode, PrincipalDto } from '../../api/types'
import { useAction, useApi } from '../../api/useApi'
import { can } from '../../app/navigation'
import { useMe } from '../../auth/SessionContext'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { Modal } from '../../components/Modal'
import { PageHeader } from '../../components/PageHeader'
import { Tabs } from '../../components/Tabs'
import { dateTime, longDate, timeAgo } from '../../lib/format'
import { FolderTree } from '../library/FolderTree'
import {
  accessLevels,
  cannotText,
  checkAccess,
  countChanges,
  emptyPending,
  expiryText,
  expiryToInstant,
  fromEffective,
  levelLabel,
  levelRank,
  levelTone,
  loadExpiryOptions,
  principalIcon,
  principalKind,
  resolveMembers,
  savePending,
} from './accessModel'
import type { Applying, GrantLevel, Pending } from './accessModel'
import styles from './FolderAccessPage.module.css'

const LOCK_COLOR = '#855D0B'
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

const byOrder = (a: FolderNode, b: FolderNode) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name)

/** The first folder (in tree order) the caller manages; admins can repair any, so the first one. */
function defaultFolder(folders: FolderNode[], admin: boolean) {
  const ordered: FolderNode[] = []
  const walk = (parentId: string | null) => {
    for (const f of folders.filter((x) => (x.parentId ?? null) === parentId).sort(byOrder)) {
      ordered.push(f)
      walk(f.id)
    }
  }
  walk(null)
  return ordered.find((f) => f.myLevel === 'Manage') ?? (admin ? ordered[0] : undefined)
}

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`

/** Loads the folder and its library tree, and keeps unsaved changes per folder while on the page. */
export function FolderAccessPage() {
  const { folderId = '' } = useParams()
  const me = useMe()
  const navigate = useNavigate()
  const known = GUID.test(folderId)
  const tree = useApi((signal) => getFolderTree('library', signal), [])
  const detail = useApi((signal) => (known ? getFolder(folderId, signal) : Promise.resolve(undefined)), [folderId])
  const [opened, setOpened] = useState<Set<string>>(() => new Set())
  const [drafts, setDrafts] = useState<Record<string, Pending>>({})

  const unknown = !known || (detail.error instanceof ApiError && (detail.error.isNotFound || detail.error.isForbidden))
  if (unknown) {
    if (tree.error) return <ErrorState error={tree.error} onRetry={tree.reload} />
    if (!tree.data) return <Loading />
    const fallback = defaultFolder(tree.data, can(me, 'isAdmin'))
    if (fallback && fallback.id !== folderId) return <Navigate to={`/library/folders/${fallback.id}/access`} replace />
    return (
      <>
        <PageHeader title="Folder access" />
        <section className={styles.panel}>
          <h2 className={styles.panelTitle}>No folders to manage</h2>
          <p className={styles.mutedSmall}>
            You can&apos;t change access on any library folder. A folder&apos;s managers or an admin can change who has access.
          </p>
        </section>
      </>
    )
  }
  if (detail.error) return <ErrorState error={detail.error} onRetry={detail.reload} />
  if (!detail.data) return <Loading />

  const current = detail.data
  const id = current.folder.id
  // Remount per folder so local edits don't leak between folders.
  if (current.folder.isSystem) return <SystemFolderAccess key={id} detail={current} />
  return (
    <LibraryFolderAccess
      key={id}
      detail={current}
      folders={tree.data}
      treeError={tree.error}
      expanded={new Set([...opened, ...current.breadcrumbs.map((b) => b.id)])}
      onSelect={(f) => {
        setOpened((prev) => new Set([...prev, f.id]))
        navigate(`/library/folders/${f.id}/access`)
      }}
      pending={drafts[id] ?? emptyPending()}
      setPending={(next) => setDrafts((prev) => ({ ...prev, [id]: next }))}
      onSaved={() => {
        tree.reload()
        detail.reload()
      }}
    />
  )
}

// ---------------------------------------------------------------------------
// Library folders: own vs inherited grants, five levels, expiry, stop inheriting
// ---------------------------------------------------------------------------

interface LibraryFolderAccessProps {
  detail: FolderDetail
  folders: FolderNode[] | undefined
  treeError: unknown
  expanded: Set<string>
  onSelect: (folder: FolderNode) => void
  pending: Pending
  setPending: (next: Pending) => void
  onSaved: () => void
}

function LibraryFolderAccess({ detail, folders, treeError, expanded, onSelect, pending, setPending, onSaved }: LibraryFolderAccessProps) {
  const navigate = useNavigate()
  const me = useMe()
  const folder = detail.folder
  const path = detail.breadcrumbs
  const parent = path.length > 1 ? path[path.length - 2] : undefined
  const isAdmin = can(me, 'isAdmin')
  const canAudit = can(me, 'canViewAudit')
  const myKey = `user:${me.id}`

  const [tab, setTab] = useState('access')
  const [dialog, setDialog] = useState<'stop' | 'resume' | null>(null)
  const [copyInherited, setCopyInherited] = useState(true)
  const [selfConfirm, setSelfConfirm] = useState<Pending | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [guard, setGuard] = useState<string | null>(null)
  const [saveFailure, setSaveFailure] = useState<{ error: unknown; saved: number; total: number } | null>(null)

  // Add-access form
  const [pickerQuery, setPickerQuery] = useState('')
  const [query, setQuery] = useState('')
  const [pickerOpen, setPickerOpen] = useState(false)
  const [picked, setPicked] = useState<PrincipalDto | null>(null)
  const [newLevel, setNewLevel] = useState<GrantLevel>('View')
  const [newExpiry, setNewExpiry] = useState('')
  const pickerRef = useRef<HTMLDivElement>(null)

  // Check someone's access
  const [checkId, setCheckId] = useState(me.id)

  const canRead = detail.allowedActions.includes('manageAccess')
  const access = useApi((signal) => (canRead ? getAccess('folders', folder.id, signal) : Promise.resolve(undefined)), [canRead, folder.id])
  const resuming = pending.inheritance === 'resume'
  const parentAccess = useApi(
    (signal) => (parent && resuming ? getAccess('folders', parent.id, signal) : Promise.resolve(undefined)),
    [parent?.id, resuming],
  )
  const expiryOptions = useApi(() => loadExpiryOptions(), [])
  const matches = useApi((signal) => (pickerOpen ? searchPrincipals(query, signal) : Promise.resolve(undefined)), [pickerOpen, query])
  const people = useApi((signal) => searchPeople('', 1, 100, signal), [])
  const audit = useApi(
    (signal) =>
      canAudit && tab === 'activity'
        ? listAuditEvents({ resourceType: 'folder', resourceId: folder.id, pageSize: 50 }, signal)
        : Promise.resolve(undefined),
    [canAudit, tab, folder.id],
  )
  const save = useAction(() => savePending(folder.id, pending, access.data?.grants ?? [], myKey))

  useEffect(() => {
    if (!pickerOpen) return
    const onDown = (e: MouseEvent) => {
      if (!pickerRef.current?.contains(e.target as Node)) setPickerOpen(false)
    }
    document.addEventListener('mousedown', onDown)
    return () => document.removeEventListener('mousedown', onDown)
  }, [pickerOpen])

  useEffect(() => {
    const t = window.setTimeout(() => setQuery(pickerQuery.trim()), 250)
    return () => window.clearTimeout(t)
  }, [pickerQuery])

  useEffect(() => {
    if (!notice) return
    const t = window.setTimeout(() => setNotice(null), 4000)
    return () => window.clearTimeout(t)
  }, [notice])

  const a = access.data
  const grants = a?.grants ?? []
  const editable = Boolean(a?.canManage)
  const inheritsNow = a?.inheritPermissions ?? folder.inheritPermissions
  const inheritsAfter = Boolean(parent) && (pending.inheritance === 'stop' ? false : resuming ? true : inheritsNow)
  const inheritedNow = (a?.effective ?? []).filter((e) => e.inherited)
  /** Grants on the parent that apply here after saving; unknown while resuming until the parent's grants load. */
  const inheritedFor = (p: Pending) => {
    if (!parent || p.inheritance === 'stop' || (!inheritsNow && p.inheritance !== 'resume')) return []
    return inheritsNow ? inheritedNow : (parentAccess.data?.effective ?? [])
  }
  const inherited = inheritedFor(pending)
  const levelOf = (grantId: string, level: AccessLevel) => pending.levels[grantId] ?? level

  const meAsPrincipal: PrincipalDto = { type: 'User', id: me.id, key: myKey, name: me.displayName, detail: 'Person' }
  const nowRows: Applying[] = (a?.effective ?? []).map((e) => fromEffective(e, grants))
  /** Grants that will apply after saving `p`, before the API's own adjustment (see keepsManager). */
  const savedRows = (p: Pending): Applying[] => [
    ...grants
      .filter((g) => !g.expired && !p.removed.includes(g.id))
      .map((g) => ({
        principal: g.principal,
        level: p.levels[g.id] ?? g.level,
        source: folder.name,
        inherited: false,
        expiresAt: g.expiresAt,
      })),
    ...p.added.map((g) => ({ principal: g.principal, level: g.level, source: folder.name, inherited: false, expiresAt: g.expiresAt })),
    ...inheritedFor(p).map((e) => fromEffective({ ...e, inherited: true })),
  ]

  // Which people each grant stands for, to check someone's access and count who gains or loses.
  const principalsInView = [...nowRows, ...savedRows(pending)].map((r) => r.principal).concat(meAsPrincipal)
  const memberKeys = [...new Set(principalsInView.map((p) => p.key))].sort().join(',')
  const members = useApi(() => resolveMembers(principalsInView), [memberKeys])
  const coversMe = (p: PrincipalDto) => p.key === myKey || Boolean(members.data?.get(p.key)?.has(me.id))

  /**
   * Stopping inheritance keeps a non-admin editor's Manage: the API adds a grant for them when none of
   * their own grants is Manage at that point (it runs after additions and level changes, before removals).
   */
  const keepsManager = (p: Pending) =>
    p.inheritance === 'stop' &&
    !isAdmin &&
    ![...grants.filter((g) => !g.expired).map((g) => ({ principal: g.principal, level: p.levels[g.id] ?? g.level })), ...p.added].some(
      (r) => r.level === 'Manage' && coversMe(r.principal),
    )
  const rowsAfter = (p: Pending): Applying[] => [
    ...savedRows(p),
    ...(keepsManager(p) ? [{ principal: meAsPrincipal, level: 'Manage' as const, source: folder.name, inherited: false }] : []),
  ]
  const afterRows = rowsAfter(pending)

  const changeCount = countChanges(pending)
  const manageCount = (p: Pending) => rowsAfter(p).filter((r) => r.level === 'Manage').length
  const myTop = (p: Pending) => checkAccess(rowsAfter(p), members.data, me.id).top
  const tryPending = (next: Pending, message: string) => {
    const parentUnknown = next.inheritance === 'resume' && !parentAccess.data
    if (!parentUnknown && manageCount(pending) > 0 && manageCount(next) === 0) {
      setGuard(message)
      return
    }
    setGuard(null)
    if (!isAdmin && members.data && myTop(pending) === 'Manage' && myTop(next) !== 'Manage') {
      setSelfConfirm(next)
      return
    }
    setPending(next)
  }

  const addGrant = () => {
    if (!picked) return
    setPending({ ...pending, added: [...pending.added, { principal: picked, level: newLevel, expiresAt: expiryToInstant(newExpiry) }] })
    setPicked(null)
    setPickerQuery('')
    setNewLevel('View')
    setNewExpiry('')
  }

  const stopInheriting = () => {
    const next: Pending = {
      ...pending,
      inheritance: 'stop',
      added: [...pending.added],
      levels: { ...pending.levels },
      copiedLevels: [...pending.copiedLevels],
    }
    if (copyInherited) {
      for (const e of inheritedNow) {
        if (e.level === 'None') continue
        const own = grants.find((g) => g.principal.key === e.principal.key && !g.expired && !pending.removed.includes(g.id))
        if (!own) next.added.push({ principal: e.principal, level: e.level, expiresAt: null, copied: true })
        else if (levelRank(e.level) > levelRank(levelOf(own.id, own.level))) {
          next.levels[own.id] = e.level
          next.copiedLevels.push(own.id)
        }
      }
    }
    if (manageCount(pending) > 0 && manageCount(next) === 0)
      setGuard('Keep at least one Manage grant: copy the inherited grants or add a manager first.')
    else {
      setGuard(null)
      setPending(next)
    }
    setDialog(null)
  }

  const undoStop = () => {
    const levels = { ...pending.levels }
    for (const id of pending.copiedLevels) delete levels[id]
    setPending({ ...pending, inheritance: undefined, added: pending.added.filter((g) => !g.copied), levels, copiedLevels: [] })
  }

  const onSave = async () => {
    const result = await save.run()
    if (!result) return
    setGuard(null)
    if (result.error) {
      setPending(result.remaining)
      setSaveFailure({ error: result.error, saved: result.saved, total: result.total })
    } else {
      setPending(emptyPending())
      setSaveFailure(null)
      setNotice(`Access saved · ${plural(changeCount, 'change')}. Permission checks apply now.`)
    }
    access.reload()
    audit.reload()
    onSaved()
  }

  // Impact of saving: this folder and the subfolders that inherit from it.
  const inheritingDescendants: FolderNode[] = []
  const stoppedDescendants: FolderNode[] = []
  const walk = (id: string) => {
    for (const child of (folders ?? []).filter((f) => f.parentId === id)) {
      if (!child.inheritPermissions) stoppedDescendants.push(child)
      else {
        inheritingDescendants.push(child)
        walk(child.id)
      }
    }
  }
  walk(folder.id)
  const affectedDocs = [folder, ...inheritingDescendants].reduce((n, f) => n + f.documentCount, 0)
  const countsDocs = a ? a.myLevel !== 'None' : false

  const impactLine = (p: PrincipalDto, gains: boolean, level: AccessLevel) => {
    const verb = gains ? 'gain' : 'lose'
    if (p.type === 'User') return `${p.name} ${verb}s ${level}`
    const n = members.data?.get(p.key)?.size
    if (n === undefined) return `Members of ${p.name} ${verb} ${level}`
    return `${plural(n, 'person', 'people')} in ${p.name} ${n === 1 ? `${verb}s` : verb} ${level}`
  }
  const impactLines: string[] = []
  for (const g of pending.added) if (!g.copied) impactLines.push(impactLine(g.principal, true, g.level))
  for (const id of pending.removed) {
    const g = grants.find((x) => x.id === id)
    if (g && !g.expired) impactLines.push(impactLine(g.principal, false, g.level))
  }
  for (const [id, level] of Object.entries(pending.levels)) {
    const g = grants.find((x) => x.id === id)
    if (!g || pending.copiedLevels.includes(id)) continue
    const up = levelRank(level) > levelRank(g.level)
    impactLines.push(impactLine(g.principal, up, up ? level : g.level))
  }

  // Check someone's access
  const now = checkAccess(nowRows, members.data, checkId)
  const after = checkAccess(afterRows, members.data, checkId)
  const checkBadge = () => {
    if (after.top === now.top) return <Badge tone={now.top ? 'success' : 'neutral'}>{now.top ? levelLabel(now.top) : 'No access'}</Badge>
    const gained = after.top && (!now.top || levelRank(after.top) > levelRank(now.top))
    return <Badge tone={gained ? 'success' : 'danger'}>{after.top ? levelLabel(after.top) : 'No access'} (after save)</Badge>
  }
  const cannot = cannotText(after.top)
  const unchecked = after.top === 'Manage' ? [] : after.unknownGroups

  const taken = new Set([
    ...grants.filter((g) => !g.expired && !pending.removed.includes(g.id)).map((g) => g.principal.key),
    ...pending.added.map((g) => g.principal.key),
  ])
  const pickerMatches = (matches.data ?? []).filter((p) => !taken.has(p.key))

  // Names for the activity log (revocations only record the principal key).
  const names = new Map<string, string>()
  for (const p of [...principalsInView, ...grants.map((g) => g.principal)]) names.set(p.key, p.name)
  const auditText = (row: AuditRow) => {
    const m = (row.metadata ?? {}) as Record<string, unknown>
    const level = typeof m.level === 'string' ? levelLabel(m.level as AccessLevel) : ''
    const name = String(m.principalName ?? names.get(String(m.principal)) ?? 'A grant')
    switch (row.action) {
      case 'folder.access-granted':
        return `${name} · ${level} granted${m.expiresAt ? `, until ${longDate(String(m.expiresAt))}` : ''}`
      case 'folder.access-revoked':
        return `${name} · ${level} removed`
      case 'folder.inheritance-stopped':
        return `Stopped inheriting${parent ? ` from ${parent.name}` : ''}`
      case 'folder.inheritance-restored':
        return `Inheriting${parent ? ` from ${parent.name}` : ''} again`
      case 'folder.created':
        return `${String(m.name ?? folder.name)} created`
      case 'folder.moved':
        return 'Moved to another folder'
      case 'folder.updated':
        return 'Folder details changed'
      default:
        return row.action
    }
  }

  const levelSelect = (value: AccessLevel, onChange: (l: GrantLevel) => void, label: string) => (
    <span className={styles.selectWrap}>
      <select className={styles.levelSelect} value={value} aria-label={label} onChange={(e) => onChange(e.target.value as GrantLevel)}>
        {accessLevels.map((l) => (
          <option key={l.key} value={l.key}>
            {l.label}
          </option>
        ))}
      </select>
      <ChevronDown size={14} className={styles.selectChevron} aria-hidden />
    </span>
  )

  const accessColumn = () => (
    <>
      {banner}
      {access.error ? (
        <ErrorState error={access.error} onRetry={access.reload} />
      ) : !canRead ? (
        <ManagersOnly folder={folder} />
      ) : !a ? (
        <Loading label="Loading access…" />
      ) : (
        grantsAndChecks()
      )}
    </>
  )

  const banner = !parent ? (
    <div className={`${styles.banner} ${styles.bannerNeutral}`}>
      <GitFork size={20} aria-hidden />
      <div className={styles.bannerText}>
        <strong>Top-level folder</strong>
        <span>Grants here flow down to every subfolder and document inside {folder.name}, unless a subfolder stops inheriting.</span>
      </div>
    </div>
  ) : pending.inheritance === 'stop' ? (
    <div className={`${styles.banner} ${styles.bannerWarning}`}>
      <GitFork size={20} aria-hidden />
      <div className={styles.bannerText}>
        <strong>Will stop inheriting from {parent.name} on save</strong>
        <span>
          Only the grants below will apply here. Grants on {parent.name} won&apos;t.
          {keepsManager(pending) && ' You get your own Can manage grant here, so you can keep managing it.'}
        </span>
      </div>
      <Button onClick={undoStop}>Undo</Button>
    </div>
  ) : !inheritsNow && !resuming ? (
    <div className={`${styles.banner} ${styles.bannerWarning}`}>
      <FolderLock size={20} aria-hidden />
      <div className={styles.bannerText}>
        <strong>Not inheriting from {parent.name}</strong>
        <span>Only the grants set on {folder.name} apply here and to its subfolders.</span>
      </div>
      {editable && <Button onClick={() => setDialog('resume')}>Resume inheriting…</Button>}
    </div>
  ) : (
    <div className={styles.banner}>
      <GitFork size={20} aria-hidden />
      <div className={styles.bannerText}>
        <strong>{resuming ? `Will inherit from ${parent.name} again on save` : `Inheriting access from ${parent.name}`}</strong>
        <span>
          Grants on {parent.name} apply here and to everything inside {folder.name}. Grants added here apply to this folder and its
          subfolders only.
        </span>
      </div>
      {resuming ? (
        <Button onClick={() => setPending({ ...pending, inheritance: undefined })}>Undo</Button>
      ) : (
        editable && <Button onClick={() => setDialog('stop')}>Stop inheriting…</Button>
      )}
    </div>
  )

  const grantsAndChecks = () => (
    <>
      {editable && (
        <div className={styles.addRow}>
          <div className={styles.picker} ref={pickerRef}>
            <UserPlus size={16} className={styles.pickerIcon} aria-hidden />
            {picked ? (
              <span className={styles.pickedChip}>
                {picked.name}
                <button type="button" aria-label="Clear" onClick={() => setPicked(null)}>
                  <X size={12} />
                </button>
              </span>
            ) : (
              <input
                value={pickerQuery}
                placeholder="Add people, groups, colleges, departments or programs…"
                onFocus={() => setPickerOpen(true)}
                onChange={(e) => {
                  setPickerQuery(e.target.value)
                  setPickerOpen(true)
                }}
              />
            )}
            {pickerOpen && !picked && (
              <div className={styles.pickerMenu} role="listbox">
                {matches.error ? (
                  <div className={styles.pickerEmpty}>
                    <InlineError error={matches.error} />
                  </div>
                ) : matches.loading && !matches.data ? (
                  <Loading compact label="Searching…" />
                ) : (
                  pickerMatches.length === 0 && <div className={styles.pickerEmpty}>No matches</div>
                )}
                {pickerMatches.map((p) => {
                  const Icon = principalIcon(p)
                  return (
                    <button
                      key={p.key}
                      type="button"
                      role="option"
                      aria-selected={false}
                      onClick={() => {
                        setPicked(p)
                        setPickerOpen(false)
                      }}
                    >
                      <Icon size={16} aria-hidden />
                      <span className={styles.who}>
                        <strong>{p.name}</strong>
                        <span>{p.detail}</span>
                      </span>
                    </button>
                  )
                })}
              </div>
            )}
          </div>
          <span className={styles.selectWrap}>
            <select
              className={styles.pill}
              value={newLevel}
              aria-label="Access level"
              onChange={(e) => setNewLevel(e.target.value as GrantLevel)}
            >
              {accessLevels.map((l) => (
                <option key={l.key} value={l.key} title={l.description}>
                  {l.label}
                </option>
              ))}
            </select>
            <ChevronDown size={14} className={styles.selectChevron} aria-hidden />
          </span>
          <span className={`${styles.selectWrap} ${styles.withIcon}`}>
            <Calendar size={14} className={styles.selectIcon} aria-hidden />
            <select className={styles.pill} value={newExpiry} aria-label="Expiry" onChange={(e) => setNewExpiry(e.target.value)}>
              {(expiryOptions.data ?? [{ date: '', label: 'No expiry' }]).map((o) => (
                <option key={o.date} value={o.date}>
                  {o.label}
                </option>
              ))}
            </select>
            <ChevronDown size={14} className={styles.selectChevron} aria-hidden />
          </span>
          <Button variant="primary" disabled={!picked} onClick={addGrant}>
            Add
          </Button>
        </div>
      )}

      {guard && (
        <div className={styles.guard} role="alert">
          <Shield size={14} aria-hidden />
          {guard}
        </div>
      )}
      {saveFailure && (
        <div>
          <InlineError error={saveFailure.error} />
          <p className={styles.mutedSmall}>
            Saved {saveFailure.saved} of {plural(saveFailure.total, 'change')}; the rest are still unsaved below.
          </p>
        </div>
      )}

      <div className={styles.grants}>
        <div className={`${styles.grantRow} ${styles.grantHead}`}>
          <span>Who</span>
          <span>Access</span>
          <span>Source</span>
          <span>Expires</span>
          <span />
        </div>
        {resuming && parentAccess.loading && <Loading compact label={`Loading ${parent?.name}'s grants…`} />}
        {resuming && parentAccess.error !== undefined && (
          <div className={styles.noGrants}>Grants on {parent?.name} will apply here again; you can&apos;t see them from here.</div>
        )}
        {inherited.map((e) => {
          const Icon = principalIcon(e.principal)
          return (
            <div key={`inherited-${e.principal.key}`} className={styles.grantRow}>
              <span className={styles.whoCell}>
                <Icon size={18} className={styles.mutedIcon} aria-hidden />
                <span className={styles.who}>
                  <strong>
                    {e.principal.name}
                    {e.principal.key === myKey ? ' (you)' : ''}
                  </strong>
                  <span>{e.principal.detail}</span>
                </span>
              </span>
              <span className={styles.mutedText}>{levelLabel(e.level)}</span>
              <span className={styles.source}>
                <CornerLeftUp size={14} aria-hidden />
                Inherited · {e.source.name}
              </span>
              <span className={styles.mutedSmall}>—</span>
              <span />
            </div>
          )
        })}
        {grants.map((g) => {
          const p = g.principal
          const Icon = principalIcon(p)
          const removed = pending.removed.includes(g.id)
          const changed = pending.levels[g.id] !== undefined
          const expiry = <span className={g.expired ? styles.expired : styles.mutedSmall}>{expiryText(g.expiresAt, g.expired)}</span>
          if (removed) {
            return (
              <div key={g.id} className={`${styles.grantRow} ${styles.removed}`}>
                <span className={styles.whoCell}>
                  <Icon size={18} aria-hidden />
                  <span className={styles.who}>
                    <strong>{p.name}</strong>
                    <span>{principalKind(p)} · will be removed on save</span>
                  </span>
                </span>
                <span>{levelLabel(levelOf(g.id, g.level))}</span>
                <span className={styles.source}>
                  <FolderOpen size={14} aria-hidden />
                  This folder
                </span>
                <span className={styles.small}>{expiryText(g.expiresAt, g.expired)}</span>
                <button
                  type="button"
                  className={styles.rowAction}
                  aria-label={`Keep ${p.name}`}
                  title="Undo"
                  onClick={() => setPending({ ...pending, removed: pending.removed.filter((id) => id !== g.id) })}
                >
                  <Undo2 size={18} />
                </button>
              </div>
            )
          }
          return (
            <div key={g.id} className={`${styles.grantRow} ${changed ? styles.changed : ''}`}>
              <span className={styles.whoCell}>
                <Icon size={18} className={g.expired ? styles.mutedIcon : styles.primaryIcon} aria-hidden />
                <span className={styles.who}>
                  <strong>
                    {p.name}
                    {p.key === myKey ? ' (you)' : ''}
                  </strong>
                  <span>
                    {p.detail}
                    {changed ? ' · unsaved' : ''}
                  </span>
                </span>
              </span>
              {editable && !g.expired && !g.isSystem ? (
                levelSelect(
                  levelOf(g.id, g.level),
                  (l) => {
                    const levels = { ...pending.levels }
                    if (l === g.level) delete levels[g.id]
                    else levels[g.id] = l
                    tryPending(
                      { ...pending, levels, copiedLevels: pending.copiedLevels.filter((id) => id !== g.id) },
                      "The last Manage grant on a folder can't be lowered.",
                    )
                  },
                  `Access for ${p.name}`,
                )
              ) : (
                <span className={styles.mutedText}>{levelLabel(g.level)}</span>
              )}
              {g.isSystem ? (
                <span className={styles.source}>
                  <CornerLeftUp size={14} aria-hidden />
                  Org tree · role
                </span>
              ) : (
                <span className={styles.sourceOwn}>
                  <FolderOpen size={14} aria-hidden />
                  This folder
                </span>
              )}
              {expiry}
              {editable && !g.isSystem ? (
                <button
                  type="button"
                  className={styles.rowAction}
                  aria-label={`Remove ${p.name}`}
                  onClick={() =>
                    tryPending({ ...pending, removed: [...pending.removed, g.id] }, "The last Manage grant on a folder can't be removed.")
                  }
                >
                  <X size={18} />
                </button>
              ) : (
                <span />
              )}
            </div>
          )
        })}
        {pending.added.map((g, i) => {
          const p = g.principal
          const Icon = principalIcon(p)
          return (
            <div key={`added-${p.key}`} className={`${styles.grantRow} ${styles.added}`}>
              <span className={styles.whoCell}>
                <Icon size={18} className={styles.primaryIcon} aria-hidden />
                <span className={styles.who}>
                  <strong>{p.name}</strong>
                  <span className={styles.unsaved}>
                    {p.detail} · {g.copied ? 'copied from parent · unsaved' : 'unsaved'}
                  </span>
                </span>
              </span>
              {levelSelect(
                g.level,
                (l) => setPending({ ...pending, added: pending.added.map((x, j) => (j === i ? { ...x, level: l } : x)) }),
                `Access for ${p.name}`,
              )}
              <span className={styles.sourceOwn}>
                <FolderOpen size={14} aria-hidden />
                This folder
              </span>
              <span className={styles.expiresNew}>{expiryText(g.expiresAt)}</span>
              <button
                type="button"
                className={styles.rowAction}
                aria-label={`Remove ${p.name}`}
                onClick={() =>
                  tryPending(
                    { ...pending, added: pending.added.filter((_, j) => j !== i) },
                    "The last Manage grant on a folder can't be removed.",
                  )
                }
              >
                <X size={18} />
              </button>
            </div>
          )
        })}
        {inherited.length + grants.length + pending.added.length === 0 && !(resuming && !parentAccess.data) && (
          <div className={styles.noGrants}>Nobody has access yet. Add a person, group or org unit above.</div>
        )}
      </div>

      <div className={styles.bottom}>
        <section className={styles.check}>
          <h3 className={styles.cardTitle}>
            <UserSearch size={16} className={styles.primaryIcon} aria-hidden />
            Check someone&apos;s access
          </h3>
          {people.error ? (
            <ErrorState compact error={people.error} onRetry={people.reload} />
          ) : (
            <>
              <div className={styles.checkRow}>
                <span className={styles.selectWrap}>
                  <select className={styles.checkSelect} value={checkId} aria-label="Person" onChange={(e) => setCheckId(e.target.value)}>
                    {!(people.data?.items ?? []).some((p) => p.id === me.id) && <option value={me.id}>{me.displayName}</option>}
                    {(people.data?.items ?? []).map((p) => (
                      <option key={p.id} value={p.id}>
                        {p.displayName}
                      </option>
                    ))}
                  </select>
                  <ChevronDown size={14} className={styles.selectChevron} aria-hidden />
                </span>
                {members.data ? checkBadge() : <Loading compact label="Checking…" />}
              </div>
              {members.error !== undefined && <InlineError error={members.error} />}
              {members.data && (
                <ul className={styles.reasons}>
                  {after.hits.map((r) => {
                    const via = r.principal.type === 'User' && r.principal.id === checkId ? 'a direct grant' : r.principal.name
                    const where = r.inherited
                      ? `inherited from ${r.source}`
                      : `this folder${r.expiresAt ? `, until ${longDate(r.expiresAt)}` : ''}`
                    return (
                      <li key={r.principal.key}>
                        {levelLabel(r.level)} through {via} ({where})
                      </li>
                    )
                  })}
                  {cannot && <li>{cannot}</li>}
                  {isAdmin && checkId === me.id && <li>As an admin you can still repair access here, without opening the documents</li>}
                  {unchecked.length > 0 && (
                    <li>
                      Group membership isn&apos;t shown here, so {unchecked.map((r) => r.principal.name).join(', ')} may also give access
                    </li>
                  )}
                </ul>
              )}
            </>
          )}
        </section>
        {changeCount > 0 ? (
          <section className={styles.impact}>
            <h3 className={styles.impactTitle}>
              <TriangleAlert size={16} aria-hidden />
              Impact of saving
            </h3>
            <p>
              Applies to this folder
              {inheritingDescendants.length > 0
                ? `, ${plural(inheritingDescendants.length, 'subfolder')} that inherit${inheritingDescendants.length === 1 ? 's' : ''} (${inheritingDescendants.map((f) => f.name).join(', ')})`
                : ''}
              {countsDocs ? ` and ${plural(affectedDocs, 'document')}` : ''}.
              {stoppedDescendants.length > 0 &&
                ` ${stoppedDescendants.map((f) => f.name).join(', ')} ${stoppedDescendants.length === 1 ? 'has' : 'have'} stopped inheriting, so ${stoppedDescendants.length === 1 ? "it's" : "they're"} not affected.`}
            </p>
            {impactLines.length > 0 && <p>{impactLines.join(' · ')}.</p>}
            {pending.inheritance && parent && (
              <p>
                {pending.inheritance === 'stop'
                  ? `${parent.name}'s grants will no longer apply here.`
                  : `${parent.name}'s grants will apply here again.`}
              </p>
            )}
            <p>Permission checks change as soon as you save.</p>
          </section>
        ) : (
          <section className={styles.noImpact}>
            <h3 className={styles.cardTitle}>No unsaved changes</h3>
            <p>Changes you make here are listed with their impact before you save. Every change writes an audit entry.</p>
          </section>
        )}
      </div>
    </>
  )

  return (
    <>
      <PageHeader
        breadcrumb={
          <span className={styles.crumbs}>
            <Link to="/library">Knowledge Base</Link>
            {path.map((f) => (
              <span key={f.id}>
                <span className={styles.sep}>/</span>
                <Link to={`/library?folder=${f.id}`}>{f.name}</Link>
              </span>
            ))}
          </span>
        }
        title={
          <span className={styles.titleRow}>
            <FolderOpen size={24} className={styles.titleIcon} aria-hidden />
            {folder.name}
            {!parent ? (
              <Badge tone="neutral">Top-level folder</Badge>
            ) : inheritsAfter ? (
              <Badge tone="info">Inherits from {parent.name}</Badge>
            ) : (
              <Badge tone="warning">Stopped inheriting</Badge>
            )}
          </span>
        }
        actions={
          editable && (
            <>
              <Button
                disabled={changeCount === 0 || save.pending}
                onClick={() => {
                  setPending(emptyPending())
                  setGuard(null)
                  setSaveFailure(null)
                }}
              >
                Discard
              </Button>
              <Button variant="primary" disabled={changeCount === 0 || save.pending} onClick={onSave}>
                {save.pending ? 'Saving…' : changeCount === 0 ? 'Save access' : `Save access (${plural(changeCount, 'change')})`}
              </Button>
            </>
          )
        }
      />

      <Tabs
        value={tab}
        onChange={(key) => {
          if (key === 'contents') navigate(`/library?folder=${folder.id}`)
          else setTab(key)
        }}
        items={[
          { key: 'contents', label: folder.myLevel === 'None' ? 'Contents' : `Contents (${folder.documentCount})` },
          { key: 'access', label: 'Access', icon: tab === 'access' ? Shield : undefined },
          ...(canAudit ? [{ key: 'activity', label: 'Activity' }] : []),
        ]}
      />

      {notice && (
        <div className={styles.notice} role="status">
          {notice}
        </div>
      )}

      <div className={styles.body}>
        <div className={styles.treeColumn}>
          {folders ? (
            <FolderTree folders={folders} selectedId={folder.id} expanded={expanded} lockColor={LOCK_COLOR} onSelect={onSelect}>
              <div className={styles.legend}>
                <FolderLock size={14} style={{ color: LOCK_COLOR }} aria-hidden />
                <span>= stopped inheriting</span>
              </div>
            </FolderTree>
          ) : treeError ? (
            <ErrorState compact error={treeError} />
          ) : (
            <Loading compact />
          )}
        </div>

        {tab === 'activity' && canAudit ? (
          <section className={styles.panel}>
            <h2 className={styles.panelTitle}>Access activity</h2>
            {audit.error ? (
              <ErrorState compact error={audit.error} onRetry={audit.reload} />
            ) : !audit.data ? (
              <Loading compact />
            ) : audit.data.items.length === 0 ? (
              <p className={styles.mutedSmall}>No changes recorded for {folder.name} yet.</p>
            ) : (
              <ul className={styles.audit}>
                {audit.data.items.map((row) => (
                  <li key={row.id}>
                    <span>{auditText(row)}</span>
                    <span className={styles.auditMeta} title={dateTime(row.occurredAt)}>
                      {row.actorName ?? 'System'} · {timeAgo(row.occurredAt)}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </section>
        ) : (
          <div className={styles.access}>{accessColumn()}</div>
        )}
      </div>

      {dialog === 'stop' && parent && (
        <Modal
          title={`Stop inheriting from ${parent.name}?`}
          width={500}
          onClose={() => setDialog(null)}
          footer={
            <>
              <Button onClick={() => setDialog(null)}>Cancel</Button>
              <Button variant="primary" onClick={stopInheriting}>
                Stop inheriting
              </Button>
            </>
          }
        >
          <p className={styles.dialogText}>
            Grants on {parent.name} will stop applying to {folder.name} and its subfolders. There are no deny rules: to restrict a folder,
            copy the inherited grants and remove the ones that shouldn&apos;t apply.
          </p>
          <label className={styles.checkbox}>
            <input type="checkbox" checked={copyInherited} onChange={(e) => setCopyInherited(e.target.checked)} />
            Copy the {plural(inheritedNow.length, 'inherited grant')} onto {folder.name}
          </label>
        </Modal>
      )}
      {dialog === 'resume' && parent && (
        <Modal
          title={`Inherit from ${parent.name} again?`}
          width={460}
          onClose={() => setDialog(null)}
          footer={
            <>
              <Button onClick={() => setDialog(null)}>Cancel</Button>
              <Button
                variant="primary"
                onClick={() => {
                  setPending({ ...pending, inheritance: 'resume' })
                  setDialog(null)
                }}
              >
                Resume inheriting
              </Button>
            </>
          }
        >
          <p className={styles.dialogText}>
            Everyone with access to {parent.name} will get the same access to {folder.name}. The grants set on {folder.name} stay.
          </p>
        </Modal>
      )}
      {selfConfirm && (
        <Modal
          title="Remove your own Manage access?"
          width={440}
          onClose={() => setSelfConfirm(null)}
          footer={
            <>
              <Button onClick={() => setSelfConfirm(null)}>Cancel</Button>
              <Button
                variant="danger"
                onClick={() => {
                  setPending(selfConfirm)
                  setSelfConfirm(null)
                }}
              >
                Remove my access
              </Button>
            </>
          }
        >
          <p className={styles.dialogText}>
            After saving you won&apos;t be able to change access on {folder.name} unless someone gives it back.
          </p>
        </Modal>
      )}
    </>
  )
}

// ---------------------------------------------------------------------------
// Academic system folders: access follows the org tree (roles, not people)
// ---------------------------------------------------------------------------

function SystemFolderAccess({ detail }: { detail: FolderDetail }) {
  const navigate = useNavigate()
  const me = useMe()
  const folder = detail.folder
  const path = detail.breadcrumbs
  const myKey = `user:${me.id}`
  const isAdmin = can(me, 'isAdmin')
  const canRead = detail.allowedActions.includes('manageAccess')
  const access = useApi((signal) => (canRead ? getAccess('folders', folder.id, signal) : Promise.resolve(undefined)), [canRead, folder.id])
  const people = useApi((signal) => searchPeople('', 1, 100, signal), [])
  const remove = useAction(async (grantId: string) => {
    await revokeAccess('folders', folder.id, grantId)
    access.reload()
  })
  const [checkId, setCheckId] = useState(me.id)

  const a = access.data
  const grants = a?.grants ?? []
  const inherited = (a?.effective ?? []).filter((e) => e.inherited)
  const nowRows = (a?.effective ?? []).map((e) => fromEffective(e, grants))
  const memberKeys = [...new Set(nowRows.map((r) => r.principal.key))].sort().join(',')
  const members = useApi(() => resolveMembers(nowRows.map((r) => r.principal)), [memberKeys])
  const check = checkAccess(nowRows, members.data, checkId)
  const repairOnly = isAdmin && checkId === me.id && !check.top
  const checkedName = people.data?.items.find((p) => p.id === checkId)?.displayName ?? me.displayName

  const who = (p: PrincipalDto) =>
    `${p.name}${p.type === 'User' && p.id === folder.ownerUserId ? ' · owner' : ''}${p.key === myKey ? ' (you)' : ''}`

  return (
    <>
      <PageHeader
        breadcrumb={
          <span className={styles.crumbs}>
            <Link to="/library">Library</Link>
            <span className={styles.sep}>/</span>
            <Link to="/library/academic">Academic folders</Link>
            {path.slice(1).map((f) => (
              <span key={f.id}>
                <span className={styles.sep}>/</span>
                <Link to={`/library/academic?folder=${f.id}`}>{f.name}</Link>
              </span>
            ))}
          </span>
        }
        title={
          <span className={styles.titleRow}>
            <FolderOpen size={24} className={styles.titleIcon} aria-hidden />
            {folder.name}
            <Badge tone="info">System folder</Badge>
          </span>
        }
      />
      <Tabs
        value="access"
        onChange={(key) => key === 'contents' && navigate(`/library/academic?folder=${folder.id}`)}
        items={[
          { key: 'contents', label: folder.myLevel === 'None' ? 'Contents' : `Contents (${folder.documentCount})` },
          { key: 'access', label: 'Access', icon: Shield },
        ]}
      />
      <div className={styles.body}>
        <nav className={styles.pathTree} aria-label="Folder path">
          <div className={styles.pathLabel}>Folder path</div>
          {path.map((f, i) => (
            <Link
              key={f.id}
              to={`/library/folders/${f.id}/access`}
              className={`${styles.pathRow} ${f.id === folder.id ? styles.pathActive : ''}`}
              style={{ paddingLeft: 8 + i * 14 }}
            >
              <FolderOpen size={16} aria-hidden />
              <span>{f.name}</span>
            </Link>
          ))}
        </nav>
        <div className={styles.access}>
          <div className={styles.banner}>
            <GitFork size={20} aria-hidden />
            <div className={styles.bannerText}>
              <strong>Access follows the org tree</strong>
              <span>
                Owners, Academic Supervisors, Deans and Directors get access through their roles. Nobody sets these by hand, and they follow
                the role, not the person.
              </span>
            </div>
          </div>
          {access.error ? (
            <ErrorState error={access.error} onRetry={access.reload} />
          ) : !canRead ? (
            <ManagersOnly folder={folder} />
          ) : !a ? (
            <Loading label="Loading access…" />
          ) : (
            <div className={styles.grants}>
              <div className={`${styles.grantRow} ${styles.grantHead}`}>
                <span>Who</span>
                <span>Access</span>
                <span>Source</span>
                <span>Expires</span>
                <span />
              </div>
              {grants.map((g) => {
                const Icon = principalIcon(g.principal)
                return (
                  <div key={g.id} className={styles.grantRow}>
                    <span className={styles.whoCell}>
                      <Icon size={18} className={styles.mutedIcon} aria-hidden />
                      <span className={styles.who}>
                        <strong>{who(g.principal)}</strong>
                        <span>{g.principal.detail}</span>
                      </span>
                    </span>
                    <span>
                      <Badge tone={g.expired ? 'neutral' : levelTone[g.level]}>{levelLabel(g.level)}</Badge>
                    </span>
                    {g.isSystem ? (
                      <span className={styles.source}>
                        <CornerLeftUp size={14} aria-hidden />
                        Org tree · role
                      </span>
                    ) : (
                      <span className={styles.sourceOwn}>
                        <FolderOpen size={14} aria-hidden />
                        This folder
                      </span>
                    )}
                    <span className={g.expired ? styles.expired : styles.mutedSmall}>
                      {g.isSystem ? 'Follows the role' : expiryText(g.expiresAt, g.expired)}
                    </span>
                    {a.canManage && !g.isSystem ? (
                      <button
                        type="button"
                        className={styles.rowAction}
                        aria-label={`Remove ${g.principal.name}`}
                        disabled={remove.pending}
                        onClick={() => remove.run(g.id)}
                      >
                        <X size={18} />
                      </button>
                    ) : (
                      <span />
                    )}
                  </div>
                )
              })}
              {inherited.map((e) => {
                const Icon = principalIcon(e.principal)
                return (
                  <div key={`inherited-${e.principal.key}`} className={styles.grantRow}>
                    <span className={styles.whoCell}>
                      <Icon size={18} className={styles.mutedIcon} aria-hidden />
                      <span className={styles.who}>
                        <strong>{who(e.principal)}</strong>
                        <span>{e.principal.detail}</span>
                      </span>
                    </span>
                    <span>
                      <Badge tone={levelTone[e.level]}>{levelLabel(e.level)}</Badge>
                    </span>
                    <span className={styles.source}>
                      <CornerLeftUp size={14} aria-hidden />
                      Inherited · {e.source.name}
                    </span>
                    <span className={styles.mutedSmall}>Follows the role</span>
                    <span />
                  </div>
                )
              })}
              {grants.length + inherited.length === 0 && <div className={styles.noGrants}>No roles give access to this folder yet.</div>}
            </div>
          )}
          <InlineError error={remove.error} />
          <div className={styles.bottom}>
            {a && (
              <section className={styles.check}>
                <h3 className={styles.cardTitle}>
                  <UserSearch size={16} className={styles.primaryIcon} aria-hidden />
                  Check someone&apos;s access
                </h3>
                {people.error ? (
                  <ErrorState compact error={people.error} onRetry={people.reload} />
                ) : (
                  <>
                    <div className={styles.checkRow}>
                      <span className={styles.selectWrap}>
                        <select
                          className={styles.checkSelect}
                          value={checkId}
                          aria-label="Person"
                          onChange={(e) => setCheckId(e.target.value)}
                        >
                          {!(people.data?.items ?? []).some((p) => p.id === me.id) && <option value={me.id}>{me.displayName}</option>}
                          {(people.data?.items ?? []).map((p) => (
                            <option key={p.id} value={p.id}>
                              {p.displayName}
                            </option>
                          ))}
                        </select>
                        <ChevronDown size={14} className={styles.selectChevron} aria-hidden />
                      </span>
                      {!members.data ? (
                        <Loading compact label="Checking…" />
                      ) : (
                        <Badge tone={check.top ? 'success' : 'neutral'}>
                          {check.top ? levelLabel(check.top) : repairOnly ? 'Repair only' : 'No access'}
                        </Badge>
                      )}
                    </div>
                    {members.error !== undefined && <InlineError error={members.error} />}
                    {members.data && (
                      <ul className={styles.reasons}>
                        {check.hits.map((r) => {
                          const via = r.principal.type === 'User' && r.principal.id === checkId ? 'a direct grant' : r.principal.name
                          const where = r.inherited
                            ? `inherited from ${r.source}`
                            : grants.find((g) => g.principal.key === r.principal.key)?.isSystem
                              ? r.principal.type === 'User' && r.principal.id === folder.ownerUserId
                                ? 'owner of this folder'
                                : 'role grant from the org tree'
                              : 'this folder'
                          return (
                            <li key={r.principal.key}>
                              {levelLabel(r.level)} through {via} ({where})
                            </li>
                          )
                        })}
                        {repairOnly ? (
                          <li>Admins can repair access and re-run the org sync, but can&apos;t open the documents</li>
                        ) : (
                          !check.top && <li>No role in this part of the org tree gives {checkedName} access</li>
                        )}
                        {check.top && cannotText(check.top) && <li>{cannotText(check.top)}</li>}
                      </ul>
                    )}
                  </>
                )}
              </section>
            )}
            <section className={styles.rules}>
              <h3 className={styles.cardTitle}>
                <FolderLock size={16} className={styles.primaryIcon} aria-hidden />
                System folder
              </h3>
              <ul className={styles.rulesList}>
                <li>Created by the org sync or on the first submission, never by hand</li>
                <li>Grants can&apos;t be added or removed here; admins can repair them</li>
                <li>A new AS or Dean sees the folder as soon as their role starts</li>
              </ul>
            </section>
          </div>
        </div>
      </div>
    </>
  )
}

/** Reading a folder's grants needs Manage; say so instead of asking the API for a 403. */
function ManagersOnly({ folder }: { folder: FolderNode }) {
  return (
    <section className={`${styles.noImpact} ${styles.managersOnly}`}>
      <h3 className={styles.cardTitle}>Only this folder&apos;s managers see who has access</h3>
      <p>
        {folder.myLevel === 'None' ? "You don't have access to this folder." : `Your access here is “${levelLabel(folder.myLevel)}”.`} Ask
        someone who can manage {folder.name}, or an admin, to change who has access.
      </p>
    </section>
  )
}

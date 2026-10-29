import { useEffect, useRef, useState } from 'react'
import { ChevronLeft, ChevronRight, Ellipsis, FileArchive, FileText, FolderPlus, LayoutGrid, List, Search, Upload, X } from 'lucide-react'
import { Navigate, useNavigate, useSearchParams } from 'react-router'
import { ApiError, errorMessage, isAbort } from '../../api/client'
import { archiveDocument, createFolder, getFolder, getFolderChildren, getFolderTree, restoreDocument, searchDocuments } from '../../api/library'
import type { SearchMode } from '../../api/library'
import { getAcademicYears, getDocumentTypes } from '../../api/reference'
import type { AcademicTag, AcademicYearDto, AccessLevel, DocumentListItem, DocumentState, FolderNode, SearchHit } from '../../api/types'
import { completeUpload, uploadFile } from '../../api/uploads'
import { useAction, useApi } from '../../api/useApi'
import { can } from '../../app/navigation'
import { useMe } from '../../auth/SessionContext'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { TagChip, TypeChip } from '../../components/Chips'
import { FilterChip } from '../../components/FilterChip'
import type { FilterOption } from '../../components/FilterChip'
import { Modal } from '../../components/Modal'
import { PageHeader } from '../../components/PageHeader'
import { formatBytes, longDate, shortDate, termShortLabel } from '../../lib/format'
import { FolderTree } from './FolderTree'
import { UploadModal } from './UploadModal'
import type { UploadRequest } from './UploadModal'
import { indexBadge, isProcessing, stateLabel, stateTone } from './libraryUi'
import { effectiveMode, hitLink, searchPlaceholder } from './searchModes'
import { HitSnippet, SearchModeToggle } from './searchUi'
import styles from './LibraryPage.module.css'

const PAGE_SIZE = 6

/** A page of the list: the folder's documents, or a search's matches with what each one matched on. */
interface DocumentPage {
  items: DocumentListItem[]
  total: number
  hits: Map<string, SearchHit> | null
  /** Why search by meaning didn't take part, when the AI is on but not ready. */
  notice: string | null
}
/** While a listed document's text is still being extracted, the list reloads this often. */
const PROCESSING_POLL_MS = 4000
const MAX_POLLS = 45

const LEVELS: AccessLevel[] = ['None', 'View', 'Download', 'Contribute', 'Edit', 'Manage']
const atLeast = (level: AccessLevel, min: AccessLevel) => LEVELS.indexOf(level) >= LEVELS.indexOf(min)
const byOrder = (a: FolderNode, b: FolderNode) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name)

interface UploadItem {
  key: string
  file: File
  typeId: string
  yearId?: string
  termId?: string
  folderId: string
  stage: 'waiting' | 'uploading' | 'completing' | 'stored' | 'failed'
  loaded: number
  note?: string
  error?: unknown
}

const stateOptions: FilterOption<string>[] = [
  { value: 'any', label: 'Any' },
  { value: 'Published', label: 'Published' },
  { value: 'Draft', label: 'Draft' },
  { value: 'InReview', label: 'In review' },
  { value: 'Archived', label: 'Archived' },
]

function titleFromFile(name: string) {
  const base = name.replace(/\.[a-z0-9]+$/i, '').replace(/[-_]+/g, ' ').trim()
  return base.charAt(0).toUpperCase() + base.slice(1)
}

/** The first top-level library folder the user can open; admins (who navigate everything) get the first one. */
function defaultFolder(nodes: FolderNode[]) {
  const manual = nodes.filter((f) => !f.isSystem)
  const top = manual.filter((f) => !f.parentId).sort(byOrder)
  return top.find((f) => f.accessible) ?? manual.find((f) => f.accessible) ?? top[0]
}

/** "v3 live · v4 draft", "v2 live", "v1 in review". */
function versionLabel(d: DocumentListItem) {
  const current = d.currentVersion?.number
  if (!current) return '—'
  const published = d.publishedVersionNumber
  if (d.state === 'Archived') return `v${current}`
  const pending = d.state === 'InReview' ? 'in review' : 'draft'
  if (!published) return `v${current} ${pending}`
  return published === current ? `v${current} live` : `v${published} live · v${current} ${pending}`
}

const updatedLabel = (iso: string) => (new Date(iso).getFullYear() === new Date().getFullYear() ? shortDate(iso) : longDate(iso))

function periodChip(tag: AcademicTag, yearName: string | null | undefined, term: { shortName: string; academicYearName: string } | null) {
  if (tag === 'None') return <TagChip tag="None" label="No year or term" />
  if (tag === 'Term') return <TagChip tag="Term" label={term ? termShortLabel(term) : 'Term missing'} />
  return <TagChip tag="Year" label={yearName || 'Year missing'} />
}

const docPeriodChip = (d: DocumentListItem) =>
  periodChip(
    d.type.academicTag,
    d.period.academicYearName,
    d.period.academicTermShortName && d.period.academicYearName
      ? { shortName: d.period.academicTermShortName, academicYearName: d.period.academicYearName }
      : null,
  )

function uploadPeriodChip(u: UploadItem, tag: AcademicTag | undefined, years: AcademicYearDto[]) {
  const year = years.find((y) => y.id === u.yearId)
  const term = years.flatMap((y) => y.terms).find((t) => t.id === u.termId)
  return periodChip(tag ?? 'None', year?.name, term ?? null)
}

function FileKindIcon({ archived, size }: { archived: boolean; size: number }) {
  if (archived) return <FileArchive size={size} className={styles.archiveIcon} aria-hidden />
  return <FileText size={size} className={styles.pdfIcon} aria-hidden />
}

export function LibraryPage() {
  const navigate = useNavigate()
  const me = useMe()
  const [params, setParams] = useSearchParams()
  const folderParam = params.get('folder')

  const tree = useApi((signal) => getFolderTree('all', signal), [])
  const reference = useApi(() => Promise.all([getDocumentTypes(), getAcademicYears()]), [])
  const [types, years] = reference.data ?? [[], []]
  const nodes = tree.data ?? []
  const byId = new Map(nodes.map((f) => [f.id, f]))
  const folder = folderParam ? byId.get(folderParam) : defaultFolder(nodes)
  const folderId = folder?.id

  const pathOf = (id: string) => {
    const out: FolderNode[] = []
    let cur = byId.get(id)
    while (cur) {
      out.unshift(cur)
      cur = cur.parentId ? byId.get(cur.parentId) : undefined
    }
    return out
  }
  const path = folder ? pathOf(folder.id) : []

  const [opened, setOpened] = useState<Set<string>>(new Set())
  const [collapsed, setCollapsed] = useState<string | null>(null)
  const expanded = new Set([...opened, ...path.map((f) => f.id)])
  if (collapsed) expanded.delete(collapsed)

  const [query, setQuery] = useState('')
  const [q, setQ] = useState('')
  const [mode, setMode] = useState<SearchMode>('smart')
  const [typeFilter, setTypeFilter] = useState('any')
  const [period, setPeriod] = useState('any')
  const [state, setState] = useState('any')
  const [view, setView] = useState<'list' | 'grid'>('list')
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [menuFor, setMenuFor] = useState<string | null>(null)
  const [modal, setModal] = useState<'upload' | 'folder' | null>(null)
  /** Where "New folder" creates the folder: a folder id, or null for the top level (admins only). */
  const [newFolderParent, setNewFolderParent] = useState<string | null>(null)
  const [newFolderName, setNewFolderName] = useState('')
  const [newFolderError, setNewFolderError] = useState('')
  const [uploads, setUploads] = useState<UploadItem[]>([])
  const menuRef = useRef<HTMLDivElement>(null)
  const controllers = useRef(new Set<AbortController>())
  const uploadBatch = useRef(0)

  // Search as you type, without a request per keystroke.
  useEffect(() => {
    const timer = window.setTimeout(() => setQ(query.trim()), 300)
    return () => window.clearTimeout(timer)
  }, [query])

  const detail = useApi((signal) => (folderId ? getFolder(folderId, signal) : Promise.resolve(null)), [folderId])
  // The folder's documents, or with a search, the matches in it and below (smart or exact).
  const docs = useApi((signal): Promise<DocumentPage | null> => {
    if (!folderId) return Promise.resolve(null)
    const filters = {
      typeId: typeFilter === 'any' ? undefined : typeFilter,
      yearId: period.startsWith('year:') ? period.slice(5) : undefined,
      termId: period.startsWith('term:') ? period.slice(5) : undefined,
      state: state === 'any' ? undefined : (state as DocumentState),
      page,
      pageSize: PAGE_SIZE,
    }
    if (q) {
      return searchDocuments({ q, folderId, mode, ...filters }, signal).then((result) => ({
        items: result.items.map((h) => h.document),
        total: result.total,
        hits: new Map(result.items.map((h) => [h.document.id, h])),
        notice: result.notice ?? null,
      }))
    }
    return getFolderChildren(folderId, { includeSubfolders: true, ...filters }, signal).then((contents) => ({
      items: contents.documents.items,
      total: contents.documents.total,
      hits: null,
      notice: null,
    }))
  }, [folderId, q, mode, typeFilter, period, state, page])
  const documents = docs.data ?? null
  const reloadDocs = docs.reload
  const hitFor = (id: string) => documents?.hits?.get(id)
  const openDocument = (d: DocumentListItem) => navigate(hitLink(d.id, hitFor(d.id)))

  // New uploads and re-processed versions move through text extraction on the server; reload to follow
  // them, for a few minutes per view (a stuck job shouldn't keep the page polling forever).
  const processing = documents?.items.some((d) => isProcessing(d.currentVersion?.processingStatus)) ?? false
  const pollKey = [folderId, q, mode, typeFilter, period, state, page].join('|')
  const polls = useRef({ key: '', count: 0 })
  useEffect(() => {
    if (!processing) return
    if (polls.current.key !== pollKey) polls.current = { key: pollKey, count: 0 }
    if (polls.current.count >= MAX_POLLS) return
    const timer = window.setTimeout(() => {
      polls.current.count++
      reloadDocs()
    }, PROCESSING_POLL_MS)
    return () => window.clearTimeout(timer)
  }, [processing, documents, reloadDocs, pollKey])

  useEffect(() => {
    const active = controllers.current
    return () => {
      for (const c of active) c.abort()
    }
  }, [])

  useEffect(() => {
    if (!menuFor) return
    const onDown = (e: MouseEvent) => {
      if (!menuRef.current?.contains(e.target as Node)) setMenuFor(null)
    }
    document.addEventListener('mousedown', onDown)
    return () => document.removeEventListener('mousedown', onDown)
  }, [menuFor])

  const lifecycle = useAction(async (ids: string[], restore: boolean) => {
    const failures: string[] = []
    for (const id of ids) {
      try {
        await (restore ? restoreDocument(id) : archiveDocument(id))
      } catch (error) {
        failures.push(errorMessage(error))
      }
    }
    setSelected(new Set())
    setMenuFor(null)
    reloadDocs()
    if (failures.length === 0) return true
    const reasons = [...new Set(failures)].join(' ')
    throw new Error(ids.length === 1 ? reasons : `${failures.length} of ${ids.length} couldn't be ${restore ? 'restored' : 'archived'}. ${reasons}`)
  })

  const newFolder = useAction((parentId: string | null, name: string) =>
    createFolder({ parentId, name, inheritPermissions: null, isRestricted: null }),
  )

  if (tree.error && !tree.data) {
    return (
      <>
        <PageHeader title="Library" />
        <ErrorState error={tree.error} onRetry={tree.reload} />
      </>
    )
  }
  if (!tree.data) {
    return (
      <>
        <PageHeader title="Library" />
        <Loading label="Loading folders…" />
      </>
    )
  }
  if (folder?.isSystem) return <Navigate to={`/library/academic?folder=${folder.id}`} replace />

  const actions = detail.data?.allowedActions ?? []
  const canUpload = actions.includes('upload')
  const canCreateFolder = actions.includes('createFolder')
  const canManageAccess = actions.includes('manageAccess')
  const canEdit = Boolean(folder && atLeast(folder.myLevel, 'Edit'))
  const canSeeProcessing = can(me, 'canViewProcessing')
  // Only admins add top-level folders, so with no library folder yet they are the way in.
  const isAdmin = can(me, 'isAdmin')
  const canAddFolder = folder ? canCreateFolder : isAdmin

  const typeOptions: FilterOption<string>[] = [
    { value: 'any', label: 'Any' },
    ...[...types].sort((a, b) => a.name.localeCompare(b.name)).map((t) => ({ value: t.id, label: t.name })),
  ]
  const periodOptions: FilterOption<string>[] = [
    { value: 'any', label: 'Any' },
    ...years.map((y) => ({ value: `year:${y.id}`, label: y.name })),
    ...years.flatMap((y) => y.terms.map((t) => ({ value: `term:${t.id}`, label: termShortLabel(t) }))),
  ]

  const total = documents?.total ?? 0
  const pageDocs = documents?.items ?? []
  const pageCount = Math.max(1, Math.ceil(total / PAGE_SIZE))
  const currentPage = Math.min(page, pageCount)
  const allOnPageSelected = pageDocs.length > 0 && pageDocs.every((d) => selected.has(d.id))
  const filtersActive = Boolean(query || typeFilter !== 'any' || period !== 'any' || state !== 'any')

  const withPageReset = (set: (value: string) => void) => (value: string) => {
    set(value)
    setPage(1)
  }

  const resetView = () => {
    setPage(1)
    setSelected(new Set())
    setMenuFor(null)
    lifecycle.clearError()
  }

  const selectFolder = (f: FolderNode) => {
    if (f.isSystem) {
      navigate(`/library/academic?folder=${f.id}`)
      return
    }
    if (f.id === folder?.id && f.hasChildren && expanded.has(f.id)) {
      setCollapsed(f.id)
      return
    }
    setCollapsed(null)
    setOpened((prev) => new Set([...prev, ...pathOf(f.id).map((p) => p.id)]))
    setParams({ folder: f.id })
    resetView()
  }

  const clearFilters = () => {
    setQuery('')
    setQ('')
    setTypeFilter('any')
    setPeriod('any')
    setState('any')
    setPage(1)
  }

  const startUpload = async (req: UploadRequest) => {
    if (!folder) return
    setModal(null)
    const batch = ++uploadBatch.current
    const items: UploadItem[] = req.files.map((file, i) => ({
      key: `${batch}-${i}`,
      file,
      typeId: req.typeId,
      yearId: req.yearId,
      termId: req.termId,
      folderId: folder.id,
      stage: 'waiting',
      loaded: 0,
    }))
    setUploads((prev) => [...items, ...prev])
    const patch = (key: string, change: Partial<UploadItem>) =>
      setUploads((prev) => prev.map((u) => (u.key === key ? { ...u, ...change } : u)))

    for (const item of items) {
      const controller = new AbortController()
      controllers.current.add(controller)
      try {
        patch(item.key, { stage: 'uploading' })
        const uploadId = await uploadFile(item.file, {
          signal: controller.signal,
          onProgress: (p) => patch(item.key, { loaded: p.loaded }),
        })
        patch(item.key, { stage: 'completing', loaded: item.file.size })
        const doc = await completeUpload(uploadId, {
          folderId: item.folderId,
          documentTypeId: item.typeId,
          title: titleFromFile(item.file.name),
          description: null,
          academicYearId: item.yearId ?? null,
          academicTermId: item.termId ?? null,
          tags: null,
          documentId: null,
          changeDescription: null,
        })
        patch(item.key, {
          stage: 'stored',
          note: `Stored${doc.documentNumber ? ` as ${doc.documentNumber}` : ''} · v1 created · queued for text extraction`,
        })
        polls.current.count = 0
        reloadDocs()
      } catch (error) {
        if (isAbort(error)) return
        patch(item.key, { stage: 'failed', error })
      } finally {
        controllers.current.delete(controller)
      }
    }
  }

  const openNewFolder = () => {
    setNewFolderParent(folder && canCreateFolder ? folder.id : null)
    setModal('folder')
  }

  const newFolderParentNode = newFolderParent ? byId.get(newFolderParent) : undefined

  const submitNewFolder = async () => {
    const name = newFolderName.trim()
    if (!name) {
      setNewFolderError('Give the folder a name.')
      return
    }
    if (nodes.some((f) => (f.parentId ?? null) === newFolderParent && f.name.toLowerCase() === name.toLowerCase())) {
      setNewFolderError(`${newFolderParentNode?.name ?? 'The library'} already has a folder called “${name}”.`)
      return
    }
    const created = await newFolder.run(newFolderParent, name)
    if (!created) return
    tree.reload()
    if (newFolderParent) setOpened((prev) => new Set([...prev, newFolderParent]))
    setParams({ folder: created.folder.id })
    resetView()
    setModal(null)
    setNewFolderName('')
    setNewFolderError('')
  }

  const closeFolderModal = () => {
    setModal(null)
    newFolder.clearError()
    setNewFolderError('')
  }

  const inProgress = uploads.filter((u) => u.stage !== 'stored' && u.stage !== 'failed').length
  const title = folder?.name ?? 'Library'
  const crumbs = [
    <span key="kb">Knowledge Base</span>,
    ...path.slice(0, -1).map((f) => (
      <button key={f.id} type="button" className={styles.crumb} onClick={() => selectFolder(f)}>
        {f.name}
      </button>
    )),
    ...(folder ? [<span key="current">{folder.name}</span>] : []),
  ]

  const pageButtons: (number | 'gap')[] = []
  for (let n = 1; n <= pageCount; n++) {
    if (n === 1 || n === pageCount || Math.abs(n - currentPage) <= 1) pageButtons.push(n)
    else if (pageButtons[pageButtons.length - 1] !== 'gap') pageButtons.push('gap')
  }

  const rowMenu = (d: DocumentListItem) =>
    menuFor === d.id && (
      <div className={styles.rowMenu} ref={menuRef} onClick={(e) => e.stopPropagation()}>
        <button type="button" onClick={() => navigate(`/documents/${d.id}`)}>
          Open
        </button>
        {canManageAccess && (
          <button type="button" onClick={() => navigate(`/library/folders/${d.folder.id}/access`)}>
            Manage folder access
          </button>
        )}
        {canEdit &&
          (d.lifecycle === 'Archived' ? (
            <button type="button" disabled={lifecycle.pending} onClick={() => void lifecycle.run([d.id], true)}>
              Restore from archive
            </button>
          ) : (
            <button
              type="button"
              className={styles.dangerItem}
              disabled={lifecycle.pending}
              onClick={() => void lifecycle.run([d.id], false)}
            >
              Archive
            </button>
          ))}
      </div>
    )

  /** "Index failed · retry" only for those who can retry it (on the Processing screen). */
  const indexOf = (d: DocumentListItem) => {
    const status = d.currentVersion?.processingStatus
    const badge = indexBadge(status)
    return status === 'Failed' && !canSeeProcessing ? { ...badge, label: 'Index failed' } : badge
  }

  const indexCell = (d: DocumentListItem) => {
    const badge = indexOf(d)
    if (d.currentVersion?.processingStatus !== 'Failed' || !canSeeProcessing) return <Badge tone={badge.tone}>{badge.label}</Badge>
    return (
      <button
        type="button"
        className={styles.badgeButton}
        title="Open processing to retry"
        onClick={(e) => {
          e.stopPropagation()
          navigate('/admin/processing')
        }}
      >
        <Badge tone={badge.tone}>{badge.label}</Badge>
      </button>
    )
  }

  const fileMeta = (d: DocumentListItem) => {
    const v = d.currentVersion
    if (!v) return 'No file yet'
    return [v.fileName, v.pageCount ? `${v.pageCount} page${v.pageCount === 1 ? '' : 's'}` : null, formatBytes(v.sizeBytes)]
      .filter(Boolean)
      .join(' · ')
  }

  const orgRoot = nodes.find((f) => !f.parentId && (f.kind === 'AcademicRoot' || f.kind === 'OfficeRoot'))
  const emptyText = !folder
    ? isAdmin
      ? 'There are no library folders yet. Add one, then share it under Manage access so people can upload into it.'
      : orgRoot
        ? `No library folders are shared with you yet. Your unit's documents are under ${orgRoot.name}.`
        : 'No library folders are shared with you yet.'
    : filtersActive
      ? 'No documents match these filters.'
      : !folder.accessible
        ? isAdmin
          ? 'Share this folder under Manage access so people can upload into it. Administrators manage folders but don’t open documents.'
          : "Documents in this folder aren't shared with you."
        : 'No documents in this folder yet.'

  const listBody = () => {
    if (docs.error && !documents) return <ErrorState error={docs.error} onRetry={docs.reload} compact />
    if (!documents && folder) return <Loading label="Loading documents…" compact />
    return null
  }

  return (
    <>
      <PageHeader
        breadcrumb={
          <span className={styles.crumbs}>
            {crumbs.map((c, i) => (
              <span key={i} className={styles.crumbPart}>
                {i > 0 && <span className={styles.sep}>/</span>}
                {c}
              </span>
            ))}
          </span>
        }
        title={title}
        actions={
          <>
            <label className={styles.search}>
              <Search size={16} aria-hidden />
              <input
                value={query}
                placeholder={searchPlaceholder(mode, 'Search by topic, title or doc no.…')}
                aria-label="Search documents"
                onChange={(e) => {
                  setQuery(e.target.value)
                  setPage(1)
                }}
              />
              {query && (
                <button type="button" className={styles.clearSearch} aria-label="Clear search" onClick={() => setQuery('')}>
                  <X size={14} />
                </button>
              )}
              <SearchModeToggle
                mode={mode}
                quoted={effectiveMode(query, mode) !== mode}
                onChange={(m) => {
                  setMode(m)
                  setPage(1)
                }}
              />
            </label>
            {folder && canManageAccess && (
              <Button onClick={() => navigate(`/library/folders/${folder.id}/access`)}>Manage access</Button>
            )}
            {canAddFolder && <Button onClick={openNewFolder}>New folder</Button>}
            {folder && canUpload && (
              <Button variant="primary" onClick={() => setModal('upload')}>
                Upload PDFs
              </Button>
            )}
          </>
        }
      />

      <div className={styles.filters}>
        <FilterChip label="Type" value={typeFilter} options={typeOptions} onChange={withPageReset(setTypeFilter)} />
        <FilterChip label="Year / term" value={period} options={periodOptions} onChange={withPageReset(setPeriod)} />
        <FilterChip label="Lifecycle" value={state} options={stateOptions} onChange={withPageReset(setState)} />
        {filtersActive && (
          <button type="button" className={styles.clearFilters} onClick={clearFilters}>
            Clear
          </button>
        )}
        <span className={styles.flex} />
        {documents?.notice && (
          <span className={styles.searchNotice} title={documents.notice}>
            Showing word matches only: search by meaning isn’t available right now.
          </span>
        )}
        <span className={styles.count}>
          {documents?.hits ? `${total} result${total === 1 ? '' : 's'}` : `${total} document${total === 1 ? '' : 's'}`}
        </span>
        <div className={styles.viewToggle} role="group" aria-label="View">
          <button
            type="button"
            className={view === 'list' ? styles.viewActive : ''}
            aria-pressed={view === 'list'}
            aria-label="List view"
            onClick={() => setView('list')}
          >
            <List size={16} />
          </button>
          <button
            type="button"
            className={view === 'grid' ? styles.viewActive : ''}
            aria-pressed={view === 'grid'}
            aria-label="Grid view"
            onClick={() => setView('grid')}
          >
            <LayoutGrid size={16} />
          </button>
        </div>
      </div>

      <div className={styles.body}>
        <FolderTree folders={nodes} selectedId={folder?.id} expanded={expanded} onSelect={selectFolder} />

        <div className={styles.content}>
          {folderParam && !folder ? (
            <ErrorState error={new ApiError(404, 'not-found', 'Not found')} />
          ) : view === 'list' ? (
            <div className={styles.table}>
              <div className={`${styles.grid} ${styles.head}`}>
                {canEdit ? (
                  <input
                    type="checkbox"
                    aria-label="Select all on this page"
                    checked={allOnPageSelected}
                    onChange={() =>
                      setSelected((prev) => {
                        const next = new Set(prev)
                        for (const d of pageDocs) {
                          if (allOnPageSelected) next.delete(d.id)
                          else next.add(d.id)
                        }
                        return next
                      })
                    }
                  />
                ) : (
                  <span />
                )}
                <span>Title</span>
                <span>Doc no.</span>
                <span>Version</span>
                <span>Lifecycle</span>
                <span>Search index</span>
                <span>Updated</span>
                <span />
              </div>
              {listBody()}
              {pageDocs.map((d) => (
                <div
                  key={d.id}
                  className={`${styles.grid} ${styles.row} ${selected.has(d.id) ? styles.rowSelected : ''}`}
                  onClick={() => openDocument(d)}
                >
                  {canEdit ? (
                    <input
                      type="checkbox"
                      aria-label={`Select ${d.title}`}
                      checked={selected.has(d.id)}
                      onClick={(e) => e.stopPropagation()}
                      onChange={() =>
                        setSelected((prev) => {
                          const next = new Set(prev)
                          if (next.has(d.id)) next.delete(d.id)
                          else next.add(d.id)
                          return next
                        })
                      }
                    />
                  ) : (
                    <span />
                  )}
                  <div className={styles.titleCell}>
                    <FileKindIcon archived={d.state === 'Archived'} size={20} />
                    <div className={styles.titleText}>
                      <span className={styles.docTitle}>{d.title}</span>
                      <span className={styles.meta}>{fileMeta(d)}</span>
                      <HitSnippet hit={hitFor(d.id)} query={q} />
                    </div>
                  </div>
                  <span className={styles.cell}>{d.documentNumber ?? '—'}</span>
                  <span className={styles.cell}>{versionLabel(d)}</span>
                  <span>
                    <Badge tone={stateTone[d.state]}>{stateLabel[d.state]}</Badge>
                  </span>
                  <span>{indexCell(d)}</span>
                  <span className={styles.updated}>{updatedLabel(d.updatedAt)}</span>
                  <span className={styles.menuCell}>
                    <button
                      type="button"
                      className={styles.menuButton}
                      aria-label={`Actions for ${d.title}`}
                      onClick={(e) => {
                        e.stopPropagation()
                        setMenuFor((m) => (m === d.id ? null : d.id))
                      }}
                    >
                      <Ellipsis size={18} />
                    </button>
                    {rowMenu(d)}
                  </span>
                </div>
              ))}
              {documents && pageDocs.length === 0 && (
                <div className={styles.empty}>
                  <p>{emptyText}</p>
                  {filtersActive ? (
                    <Button size="sm" onClick={clearFilters}>
                      Clear filters
                    </Button>
                  ) : (
                    canUpload && (
                      <Button size="sm" variant="primary" icon={Upload} onClick={() => setModal('upload')}>
                        Upload PDFs
                      </Button>
                    )
                  )}
                </div>
              )}
              {!folder && (
                <div className={styles.empty}>
                  <p>{emptyText}</p>
                  {isAdmin && (
                    <Button size="sm" variant="primary" icon={FolderPlus} onClick={openNewFolder}>
                      New folder
                    </Button>
                  )}
                </div>
              )}
              <div className={styles.footer}>
                {selected.size > 0 ? (
                  <span className={styles.bulk}>
                    <strong>{selected.size} selected</strong>
                    <Button size="sm" disabled={lifecycle.pending} onClick={() => void lifecycle.run([...selected], false)}>
                      Archive
                    </Button>
                    <Button size="sm" variant="ghost" onClick={() => setSelected(new Set())}>
                      Clear selection
                    </Button>
                  </span>
                ) : (
                  <span className={styles.footerText}>
                    {total === 0
                      ? 'Showing 0 of 0'
                      : `Showing ${(currentPage - 1) * PAGE_SIZE + 1}–${Math.min(currentPage * PAGE_SIZE, total)} of ${total}`}
                    {canEdit && '  ·  Select rows to archive'}
                  </span>
                )}
                <InlineError error={lifecycle.error} />
                <Pagination pages={pageButtons} current={currentPage} count={pageCount} onPage={setPage} />
              </div>
            </div>
          ) : (
            <>
              <div className={styles.cards}>
                {listBody()}
                {pageDocs.map((d) => (
                  <button key={d.id} type="button" className={styles.card} onClick={() => openDocument(d)}>
                    <span className={styles.cardHead}>
                      <FileKindIcon archived={d.state === 'Archived'} size={22} />
                      <span className={styles.docTitle}>{d.title}</span>
                    </span>
                    <span className={styles.meta}>
                      {[d.documentNumber, versionLabel(d), d.currentVersion?.pageCount ? `${d.currentVersion.pageCount} pages` : null]
                        .filter(Boolean)
                        .join(' · ')}
                    </span>
                    <HitSnippet hit={hitFor(d.id)} query={q} />
                    <span className={styles.cardChips}>
                      <TypeChip label={`${d.type.name} · ${d.type.code}`} />
                      {docPeriodChip(d)}
                    </span>
                    <span className={styles.cardBadges}>
                      <Badge tone={stateTone[d.state]}>{stateLabel[d.state]}</Badge>
                      <Badge tone={indexOf(d).tone}>{indexOf(d).label}</Badge>
                    </span>
                    <span className={styles.updated}>Updated {updatedLabel(d.updatedAt)}</span>
                  </button>
                ))}
                {(documents || !folder) && pageDocs.length === 0 && <div className={styles.empty}>{emptyText}</div>}
              </div>
              <div className={styles.gridFooter}>
                <span className={styles.footerText}>
                  {total} document{total === 1 ? '' : 's'}
                </span>
                <Pagination pages={pageButtons} current={currentPage} count={pageCount} onPage={setPage} />
              </div>
            </>
          )}

          {uploads.length > 0 && (
            <section className={styles.tray} aria-label="Uploads">
              <div className={styles.trayHead}>
                <span className={styles.trayTitle}>
                  <Upload size={18} className={styles.trayIcon} aria-hidden />
                  Uploads · {inProgress} in progress
                </span>
                <span className={styles.trayHint}>
                  Every file needs a type, plus the year or term that type requires
                  {uploads.some((u) => u.stage === 'stored' || u.stage === 'failed') && (
                    <button
                      type="button"
                      className={styles.clearFinished}
                      onClick={() => setUploads((prev) => prev.filter((u) => u.stage !== 'stored' && u.stage !== 'failed'))}
                    >
                      Clear finished
                    </button>
                  )}
                </span>
              </div>
              {uploads.map((u) => {
                const type = types.find((t) => t.id === u.typeId)
                const pct = u.stage === 'uploading' ? Math.round((u.loaded / Math.max(1, u.file.size)) * 100) : u.stage === 'waiting' ? 0 : 100
                const status =
                  u.stage === 'waiting'
                    ? 'Waiting…'
                    : u.stage === 'uploading'
                      ? `Uploading · ${pct}%`
                      : u.stage === 'completing'
                        ? 'Checking the file and creating the document…'
                        : u.stage === 'failed'
                          ? `Failed · ${errorMessage(u.error)}`
                          : (u.note ?? 'Stored')
                return (
                  <div key={u.key} className={styles.upload}>
                    <div className={styles.uploadHead}>
                      <span className={styles.uploadName}>
                        {u.file.name}  ·  {formatBytes(u.file.size)}
                      </span>
                      <span
                        className={
                          u.stage === 'stored' ? styles.uploadDone : u.stage === 'failed' ? styles.uploadFailed : styles.uploadStatus
                        }
                      >
                        {status}
                      </span>
                    </div>
                    <div className={styles.uploadTags}>
                      <TypeChip label={type ? `${type.name} · ${type.code}` : 'No type'} />
                      {uploadPeriodChip(u, type?.tag, years)}
                    </div>
                    <div className={styles.track}>
                      <div
                        className={`${styles.bar} ${u.stage === 'stored' ? styles.barDone : ''} ${u.stage === 'failed' ? styles.barFailed : ''}`}
                        style={{ width: `${pct}%` }}
                      />
                    </div>
                  </div>
                )
              })}
            </section>
          )}
        </div>
      </div>

      {modal === 'upload' && folder && (
        <UploadModal folderName={folder.name} onClose={() => setModal(null)} onSubmit={(req) => void startUpload(req)} />
      )}
      {modal === 'folder' && (
        <Modal
          title="New folder"
          description={
            newFolderParentNode
              ? `Inside ${newFolderParentNode.name}. It inherits ${newFolderParentNode.name}'s access until you change it.`
              : 'At the top of the library. Nobody can open it until you share it under Manage access.'
          }
          width={440}
          onClose={closeFolderModal}
          footer={
            <>
              <Button onClick={closeFolderModal}>Cancel</Button>
              <Button variant="primary" icon={FolderPlus} disabled={newFolder.pending} onClick={() => void submitNewFolder()}>
                Create folder
              </Button>
            </>
          }
        >
          <label className={styles.fieldRow}>
            <span className={styles.fieldLabel}>Folder name</span>
            <input
              className="field"
              autoFocus
              value={newFolderName}
              placeholder="e.g. Guidelines"
              onChange={(e) => {
                setNewFolderName(e.target.value)
                setNewFolderError('')
                newFolder.clearError()
              }}
              onKeyDown={(e) => {
                if (e.key === 'Enter') void submitNewFolder()
              }}
            />
          </label>
          {newFolderError && <p className={styles.fieldError}>{newFolderError}</p>}
          {isAdmin && folder && canCreateFolder && (
            <label className={styles.fieldRow}>
              <span className={styles.fieldLabel}>Location</span>
              <select
                className="field"
                value={newFolderParent ?? ''}
                onChange={(e) => {
                  setNewFolderParent(e.target.value || null)
                  setNewFolderError('')
                }}
              >
                <option value={folder.id}>Inside {folder.name}</option>
                <option value="">Top level of the library</option>
              </select>
            </label>
          )}
          <InlineError error={newFolder.error} />
        </Modal>
      )}
    </>
  )
}

interface PaginationProps {
  pages: (number | 'gap')[]
  current: number
  count: number
  onPage: (page: number) => void
}

function Pagination({ pages, current, count, onPage }: PaginationProps) {
  return (
    <nav className={styles.pagination} aria-label="Pages">
      <button type="button" disabled={current <= 1} onClick={() => onPage(current - 1)} aria-label="Previous page">
        <ChevronLeft size={16} />
      </button>
      {pages.map((p, i) =>
        p === 'gap' ? (
          <span key={`gap-${i}`} className={styles.gap}>
            …
          </span>
        ) : (
          <button
            key={p}
            type="button"
            className={p === current ? styles.pageActive : ''}
            aria-current={p === current ? 'page' : undefined}
            onClick={() => onPage(p)}
          >
            {p}
          </button>
        ),
      )}
      <button type="button" disabled={current >= count} onClick={() => onPage(current + 1)} aria-label="Next page">
        <ChevronRight size={16} />
      </button>
    </nav>
  )
}

import { useEffect, useMemo, useState } from 'react'
import {
  Archive,
  ArchiveRestore,
  Building,
  Building2,
  ChevronLeft,
  ChevronRight,
  FileWarning,
  FolderOpen,
  FolderUser,
  GraduationCap,
  Landmark,
  Maximize2,
  Minimize2,
  RotateCcw,
  Trash2,
  User,
  Users,
  ZoomIn,
  ZoomOut,
} from 'lucide-react'
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router'
import { getAccess } from '../../api/access'
import { ApiError } from '../../api/client'
import {
  archiveDocument,
  compareVersions,
  deleteDocument,
  getContentLink,
  getDocument,
  openDocumentContent,
  restoreDocument,
  restoreVersion,
  updateDocument,
} from '../../api/library'
import { getAcademicYears, getDocumentTypes } from '../../api/reference'
import type {
  AccessLevel,
  DocumentDetail,
  FieldChange,
  FolderRef,
  PrincipalType,
  SubmissionStatus,
  TypeScope,
  VersionDto,
  VersionState,
} from '../../api/types'
import { completeUpload, uploadFile } from '../../api/uploads'
import { useAction, useApi } from '../../api/useApi'
import { useMe } from '../../auth/SessionContext'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Badge } from '../../components/Badge'
import type { BadgeTone } from '../../components/Badge'
import { Button } from '../../components/Button'
import { TypeChip } from '../../components/Chips'
import { Modal } from '../../components/Modal'
import { PageHeader } from '../../components/PageHeader'
import { dateTime, formatBytes, longDate, shortDate } from '../../lib/format'
import { indexBadge, isProcessing, stateLabel, stateTone } from '../library/libraryUi'
import styles from './DocumentDetailPage.module.css'

const ZOOMS = [50, 75, 100, 125, 150, 200]
type SideTab = 'details' | 'versions' | 'permissions' | 'activity'
type DialogKind = 'edit' | 'upload' | 'compare' | 'restore' | 'delete' | null

const versionTone: Record<VersionState, BadgeTone> = {
  Draft: 'neutral',
  InReview: 'warning',
  Published: 'success',
  Superseded: 'neutral',
}

const versionLabel: Record<VersionState, string> = {
  Draft: 'Draft',
  InReview: 'In review',
  Published: 'Published',
  Superseded: 'Superseded',
}

const submissionLabel: Record<SubmissionStatus, string> = {
  Draft: 'draft',
  Submitted: 'submitted',
  UnderReview: 'under review',
  RevisionRequested: 'revision requested',
  Resubmitted: 'resubmitted',
  Approved: 'approved',
  Rejected: 'rejected',
  Withdrawn: 'withdrawn',
}

const levelLabel: Record<AccessLevel, string> = {
  None: 'No access',
  View: 'Can view',
  Download: 'Can download',
  Contribute: 'Can contribute',
  Edit: 'Can edit',
  Manage: 'Can manage',
}

const principalIcons: Record<PrincipalType, typeof User> = {
  User,
  Group: Users,
  College: Building2,
  Department: Building,
  Program: GraduationCap,
  CollegeDean: Building2,
  ProgramSupervisor: GraduationCap,
  DepartmentHead: Building,
  Vpaf: Landmark,
}

const compareLabels: Record<string, string> = {
  fileName: 'File name',
  contentType: 'File type',
  sizeBytes: 'Size',
  sha256: 'SHA-256',
  pageCount: 'Pages',
  uploadedBy: 'Uploaded by',
  uploadedAt: 'Uploaded',
  changeDescription: 'What changed',
  state: 'Status',
}

function plural(name: string) {
  const lower = name.toLowerCase()
  return lower.endsWith('y') ? `${lower.slice(0, -1)}ies` : `${lower}s`
}

const typeLabel = (t: { name: string; code: string }) => `${t.name} · ${t.code}`

/** "6055…3b7a", as the design shows hashes. */
const shortSha = (sha: string) => (sha.length > 8 ? `${sha.slice(0, 4)}…${sha.slice(-4)}` : sha)

/** Org-tree folders (anything but a hand-made library folder) open in the academic folder view. */
const isSystemFolder = (f: Pick<FolderRef, 'kind'>) => f.kind !== 'Manual'
const isOrgRoot = (f: Pick<FolderRef, 'kind'>) => f.kind === 'AcademicRoot' || f.kind === 'OfficeRoot'
const folderLink = (f: FolderRef) => (isSystemFolder(f) ? `/library/academic?folder=${f.id}` : `/library?folder=${f.id}`)

function periodText(doc: DocumentDetail) {
  const { type, period } = doc
  if (type.academicTag === 'None') return `No academic year or term (${plural(type.name)} aren't tagged)`
  if (type.academicTag === 'Term') {
    return period.academicTermId
      ? `Term ${period.academicTermCode} · ${period.academicTermName} · ${period.academicYearName}`
      : 'Term missing'
  }
  return period.academicYearName ?? 'Year missing'
}

function compareValue(change: FieldChange, value: string | null) {
  if (value === null || value === '') return '—'
  if (change.field === 'sizeBytes') return formatBytes(Number(value))
  if (change.field === 'sha256') return shortSha(value)
  if (change.field === 'uploadedAt') return dateTime(value)
  if (change.field === 'state') return versionLabel[value as VersionState] ?? value
  return value
}

interface ActivityItem {
  id: string
  text: string
  who: string
  when: string
}

export function DocumentDetailPage() {
  const { documentId = '' } = useParams()
  const document = useApi((signal) => getDocument(documentId, signal), [documentId])
  // useApi keeps the previous document while the next one loads; don't show it under the new URL.
  const doc = document.data?.id === documentId ? document.data : undefined

  if (!doc) {
    return (
      <>
        <PageHeader
          breadcrumb={
            <span className={styles.crumbs}>
              <Link to="/library">Knowledge Base</Link>
            </span>
          }
          title="Document"
        />
        {document.error ? <ErrorState error={document.error} onRetry={document.reload} /> : <Loading label="Loading document…" />}
      </>
    )
  }

  return <DocumentView key={doc.id} doc={doc} onChange={document.setData} reload={document.reload} />
}

function DocumentView({
  doc,
  onChange,
  reload,
}: {
  doc: DocumentDetail
  onChange: (doc: DocumentDetail) => void
  reload: () => void
}) {
  const me = useMe()
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const { hash } = useLocation()
  const versions = doc.versions
  const published = doc.publishedVersion
  const current = doc.currentVersion
  const can = (action: string) => doc.allowedActions.includes(action)
  const canRestoreVersion = can('restoreVersion')

  const linkedVersion = Number(params.get('v')?.replace(/^v/, ''))
  const linkedPage = Number(/page=(\d+)/.exec(hash)?.[1])
  const [viewing, setViewing] = useState<number | undefined>(() =>
    versions.some((v) => v.number === linkedVersion) ? linkedVersion : (published ?? current)?.number,
  )
  const viewed = versions.find((v) => v.number === viewing) ?? current ?? undefined
  const pageCount = viewed?.pageCount ?? null
  const [page, setPage] = useState(() => (linkedPage > 0 ? (pageCount ? Math.min(linkedPage, pageCount) : linkedPage) : 1))
  const [zoom, setZoom] = useState(100)
  const [wide, setWide] = useState(false)
  const [tab, setTab] = useState<SideTab>('versions')
  const [dialog, setDialog] = useState<DialogKind>(null)
  const [restoreFrom, setRestoreFrom] = useState<VersionDto | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [sessionLog, setSessionLog] = useState<ActivityItem[]>([])

  useEffect(() => {
    if (!notice) return
    const t = window.setTimeout(() => setNotice(null), 3500)
    return () => window.clearTimeout(t)
  }, [notice])

  // A new version is extracted in the background: refresh until its status settles.
  const processing = isProcessing(current?.processingStatus)
  useEffect(() => {
    if (!processing) return
    const t = window.setInterval(reload, 4000)
    return () => window.clearInterval(t)
  }, [processing, reload])

  const log = (text: string) =>
    setSessionLog((prev) => [{ id: `${Date.now()}-${prev.length}`, text, who: 'You', when: 'just now' }, ...prev])

  const download = useAction(() => openDocumentContent(doc.id, { versionId: viewed?.id }))
  const lifecycle = useAction(async (action: 'archive' | 'restore') =>
    action === 'archive' ? archiveDocument(doc.id, doc.eTag) : restoreDocument(doc.id, doc.eTag),
  )

  const draft = current && !current.isPublished ? current : undefined
  const isSystem = isSystemFolder(doc.folder)
  const crumbs = doc.breadcrumbs.filter((f) => !isOrgRoot(f))

  const goPage = (n: number) => setPage(Math.max(1, pageCount ? Math.min(pageCount, n) : n))
  const zoomBy = (dir: 1 | -1) => {
    const i = ZOOMS.indexOf(zoom)
    setZoom(ZOOMS[Math.max(0, Math.min(ZOOMS.length - 1, i + dir))])
  }

  /** New versions show up in the activity through the version list; other actions are logged here. */
  const applied = (next: DocumentDetail, message: string, activity?: string) => {
    onChange(next)
    setNotice(message)
    if (activity) log(activity)
  }

  const titleBadges = (
    <>
      {published && <Badge tone="success">Published v{published.number}</Badge>}
      {draft && (
        <Badge tone={draft.state === 'InReview' ? 'warning' : 'neutral'}>
          {draft.state === 'InReview' ? `v${draft.number} in review` : `Draft v${draft.number} in progress`}
        </Badge>
      )}
      {doc.lifecycle === 'Archived' && <Badge tone="neutral">Archived</Badge>}
    </>
  )

  return (
    <>
      <PageHeader
        breadcrumb={
          <span className={styles.crumbs}>
            <Link to="/library">Knowledge Base</Link>
            {crumbs.map((f) => (
              <span key={f.id}>
                <span className={styles.sep}>/</span>
                <Link to={folderLink(f)}>{f.name}</Link>
              </span>
            ))}
            <span className={styles.sep}>/</span>
            <span>{doc.documentNumber ?? doc.title}</span>
          </span>
        }
        title={
          <span className={styles.titleRow}>
            {doc.title}
            {titleBadges}
          </span>
        }
        description={
          <span className={styles.tags}>
            <TypeChip label={`Type: ${typeLabel(doc.type)}`} />
            <span
              className={`${styles.period} ${doc.type.academicTag === 'Term' ? styles.periodTerm : doc.type.academicTag === 'Year' ? styles.periodYear : ''}`}
            >
              {periodText(doc)}
            </span>
          </span>
        }
        actions={
          <>
            {can('download') && viewed && (
              <Button
                disabled={download.pending}
                onClick={async () => {
                  if (await download.run()) {
                    setNotice(`Downloading v${viewed.number} · ${viewed.fileName} (link valid for 5 minutes, logged in the audit trail)`)
                    log(`Downloaded v${viewed.number}`)
                  }
                }}
              >
                Download
              </Button>
            )}
            {can('edit') && <Button onClick={() => setDialog('edit')}>Edit metadata</Button>}
            {can('addVersion') && (
              <Button variant="primary" onClick={() => setDialog('upload')}>
                Upload new version
              </Button>
            )}
          </>
        }
      />

      {notice && (
        <div className={styles.notice} role="status">
          {notice}
        </div>
      )}
      <InlineError error={download.error ?? lifecycle.error} />

      <div className={styles.body}>
        <section className={styles.viewer} aria-label="PDF preview">
          <div className={styles.toolbar}>
            <div className={styles.toolGroup}>
              {viewed ? (
                <>
                  <Badge tone={versionTone[viewed.state]}>
                    Viewing v{viewed.number} · {versionLabel[viewed.state].toLowerCase()}
                  </Badge>
                  <span className={styles.sha}>sha256 {shortSha(viewed.sha256)}</span>
                </>
              ) : (
                <Badge tone="neutral">No file yet</Badge>
              )}
            </div>
            <div className={styles.toolGroup}>
              <button type="button" className={styles.iconButton} disabled={page <= 1} onClick={() => goPage(page - 1)} aria-label="Previous page">
                <ChevronLeft size={18} />
              </button>
              <span className={styles.pageLabel}>
                Page{' '}
                <input
                  className={styles.pageInput}
                  value={page}
                  aria-label="Page number"
                  onChange={(e) => {
                    const n = Number(e.target.value)
                    if (Number.isFinite(n) && n > 0) goPage(n)
                  }}
                />
                {pageCount ? ` / ${pageCount}` : ''}
              </span>
              <button
                type="button"
                className={styles.iconButton}
                disabled={pageCount !== null && page >= pageCount}
                onClick={() => goPage(page + 1)}
                aria-label="Next page"
              >
                <ChevronRight size={18} />
              </button>
            </div>
            <div className={styles.toolGroup}>
              <button type="button" className={styles.iconButton} disabled={zoom <= ZOOMS[0]} onClick={() => zoomBy(-1)} aria-label="Zoom out">
                <ZoomOut size={18} />
              </button>
              <span className={styles.zoom}>{zoom}%</span>
              <button type="button" className={styles.iconButton} disabled={zoom >= ZOOMS[ZOOMS.length - 1]} onClick={() => zoomBy(1)} aria-label="Zoom in">
                <ZoomIn size={18} />
              </button>
              <button
                type="button"
                className={styles.iconButton}
                onClick={() => setWide((w) => !w)}
                aria-label={wide ? 'Show side panel' : 'Hide side panel'}
                aria-pressed={wide}
              >
                {wide ? <Minimize2 size={18} /> : <Maximize2 size={18} />}
              </button>
            </div>
          </div>
          <div className={`${styles.canvas} ${styles.canvasPdf}`}>
            {viewed ? (
              <PdfPreview
                documentId={doc.id}
                version={viewed}
                page={page}
                zoom={zoom}
                canDownload={can('download')}
                onDownload={() => void download.run()}
              />
            ) : (
              <div className={styles.previewEmpty}>
                <FileWarning size={22} aria-hidden />
                <span>This document has no file yet.</span>
              </div>
            )}
          </div>
        </section>

        {!wide && (
          <aside className={styles.side}>
            <div className={styles.sideTabs} role="tablist">
              {(
                [
                  ['details', 'Details'],
                  ['versions', `Versions (${versions.length})`],
                  ['permissions', 'Permissions'],
                  ['activity', 'Activity'],
                ] as [SideTab, string][]
              ).map(([key, label]) => (
                <button
                  key={key}
                  type="button"
                  role="tab"
                  aria-selected={tab === key}
                  className={`${styles.sideTab} ${tab === key ? styles.sideTabActive : ''}`}
                  onClick={() => setTab(key)}
                >
                  {label}
                </button>
              ))}
            </div>

            <div className={styles.sideBody}>
              {tab === 'versions' && (
                <>
                  <div className={styles.pointers}>
                    <span className="section-label">Version pointers</span>
                    <div className={styles.pointer}>
                      <span className={styles.pointerKey}>CurrentVersionId</span>
                      {current ? (
                        <span>
                          →  v{current.number}  {draft ? '(editable draft)' : '(same as published)'}
                        </span>
                      ) : (
                        <span className={styles.pointerNone}>→  none yet (no file)</span>
                      )}
                    </div>
                    <div className={styles.pointer}>
                      <span className={styles.pointerKey}>PublishedVersionId</span>
                      {published ? (
                        <span className={styles.pointerLive}>
                          →  v{published.number}  (approved{published.approvedInSubmission ? ` in ${published.approvedInSubmission}` : ''})
                        </span>
                      ) : (
                        <span className={styles.pointerNone}>→  none yet (not published)</span>
                      )}
                    </div>
                  </div>

                  {versions.map((v) => {
                    const isViewing = v.number === viewed?.number
                    const showActions = (v.id === draft?.id && published) || (v.state === 'Superseded' && canRestoreVersion)
                    return (
                      <div
                        key={v.id}
                        role="button"
                        tabIndex={0}
                        className={`${styles.version} ${v.isPublished ? styles.versionLive : ''} ${isViewing ? styles.versionViewing : ''}`}
                        onClick={() => setViewing(v.number)}
                        onKeyDown={(e) => {
                          if (e.key === 'Enter' || e.key === ' ') {
                            e.preventDefault()
                            setViewing(v.number)
                          }
                        }}
                      >
                        <div className={styles.versionHead}>
                          <span className={styles.versionNo}>v{v.number}</span>
                          <Badge tone={versionTone[v.state]}>{versionLabel[v.state]}</Badge>
                          {isProcessing(v.processingStatus) && <Badge tone="info">{indexBadge(v.processingStatus).label}</Badge>}
                          {isViewing && <Badge tone="info">Viewing</Badge>}
                        </div>
                        <span className={styles.versionNote}>
                          {v.changeDescription ?? (v.number === 1 ? 'Original upload' : 'No description')}
                          {v.createdFromVersionNumber && !v.changeDescription?.includes(`v${v.createdFromVersionNumber}`)
                            ? ` (from v${v.createdFromVersionNumber})`
                            : ''}
                        </span>
                        <span className={styles.versionMeta}>
                          {v.uploadedBy.id === me.id ? 'You' : v.uploadedBy.displayName} · {shortDate(v.uploadedAt)} · {formatBytes(v.sizeBytes)} · sha256{' '}
                          {shortSha(v.sha256)}
                        </span>
                        {showActions && (
                          <div className={styles.versionActions} onClick={(e) => e.stopPropagation()}>
                            {v.id === draft?.id && published && (
                              <button type="button" onClick={() => setDialog('compare')}>
                                Compare with v{published.number}
                              </button>
                            )}
                            {v.state === 'Superseded' && canRestoreVersion && current && (
                              <button
                                type="button"
                                className={styles.restore}
                                onClick={() => {
                                  setRestoreFrom(v)
                                  setDialog('restore')
                                }}
                              >
                                <RotateCcw size={14} aria-hidden />
                                Restore as new version (v{current.number + 1})
                              </button>
                            )}
                          </div>
                        )}
                      </div>
                    )
                  })}
                  <p className={styles.sideNote}>
                    Restoring never rewrites history: the chosen bytes become a new version with CreatedFromVersionId set.
                  </p>
                </>
              )}

              {tab === 'details' && (
                <>
                  <dl className={styles.details}>
                    <dt>Document no.</dt>
                    <dd>{doc.documentNumber ?? 'Not numbered yet'}</dd>
                    <dt>Document type</dt>
                    <dd>{typeLabel(doc.type)}</dd>
                    <dt>Year / term</dt>
                    <dd>{periodText(doc)}</dd>
                    {doc.description && (
                      <>
                        <dt>Description</dt>
                        <dd>{doc.description}</dd>
                      </>
                    )}
                    <dt>Folder</dt>
                    <dd>
                      <Link to={folderLink(doc.folder)}>{doc.breadcrumbs.map((f) => f.name).join(' › ')}</Link>
                    </dd>
                    <dt>Owner</dt>
                    <dd>{doc.owner.displayName}</dd>
                    <dt>File</dt>
                    <dd>
                      {current
                        ? `${current.fileName}${current.pageCount ? ` · ${current.pageCount} pages` : ''} · ${formatBytes(current.sizeBytes)}`
                        : 'No file yet'}
                    </dd>
                    <dt>Lifecycle</dt>
                    <dd>
                      <Badge tone={stateTone[doc.state]}>{stateLabel[doc.state]}</Badge>
                      {doc.onLegalHold && (
                        <>
                          {' '}
                          <Badge tone="danger">Legal hold</Badge>
                        </>
                      )}
                    </dd>
                    <dt>Search index</dt>
                    <dd>
                      <Badge tone={indexBadge(current?.processingStatus).tone}>{indexBadge(current?.processingStatus).label}</Badge>
                    </dd>
                    {doc.submissions.length > 0 && (
                      <>
                        <dt>Submission</dt>
                        <dd>{doc.submissions.map((s) => `${s.number} (${submissionLabel[s.status]})`).join(', ')}</dd>
                      </>
                    )}
                    {doc.tags.length > 0 && (
                      <>
                        <dt>Tags</dt>
                        <dd>{doc.tags.join(', ')}</dd>
                      </>
                    )}
                    <dt>Last updated</dt>
                    <dd>{longDate(doc.updatedAt)}</dd>
                  </dl>
                  {(can('archive') || can('restore') || can('delete')) && (
                    <div className={`${styles.versionActions} ${styles.lifecycleActions}`}>
                      {can('archive') && (
                        <button
                          type="button"
                          disabled={lifecycle.pending}
                          onClick={async () => {
                            const next = await lifecycle.run('archive')
                            if (next) applied(next, 'Archived. It stays readable but takes no new versions.', 'Archived the document')
                          }}
                        >
                          <Archive size={14} aria-hidden />
                          Archive document
                        </button>
                      )}
                      {can('restore') && (
                        <button
                          type="button"
                          disabled={lifecycle.pending}
                          onClick={async () => {
                            const next = await lifecycle.run('restore')
                            if (next) applied(next, 'Restored. The document is active again.', 'Restored the document')
                          }}
                        >
                          <ArchiveRestore size={14} aria-hidden />
                          Restore document
                        </button>
                      )}
                      {can('delete') && (
                        <button type="button" className={styles.danger} onClick={() => setDialog('delete')}>
                          <Trash2 size={14} aria-hidden />
                          Delete document
                        </button>
                      )}
                    </div>
                  )}
                </>
              )}

              {tab === 'permissions' && <PermissionsPanel doc={doc} isSystem={isSystem} canManage={can('manageAccess')} />}

              {tab === 'activity' && <ActivityPanel doc={doc} sessionLog={sessionLog} myId={me.id} />}
            </div>
          </aside>
        )}
      </div>

      {dialog === 'edit' && (
        <EditMetadataDialog
          doc={doc}
          onClose={() => setDialog(null)}
          onSaved={(next) => {
            setDialog(null)
            applied(next, 'Metadata saved. The search index picks up the change in about a minute.', 'Edited metadata')
          }}
          onStale={reload}
        />
      )}
      {dialog === 'upload' && (
        <UploadVersionDialog
          documentId={doc.id}
          nextNumber={(versions[0]?.number ?? 0) + 1}
          onClose={() => setDialog(null)}
          onUploaded={(next) => {
            setDialog(null)
            setTab('versions')
            const added = next.currentVersion
            if (added) setViewing(added.number)
            applied(next, `v${added?.number ?? ''} uploaded. It stays a private draft until it is approved and published.`)
          }}
        />
      )}
      {dialog === 'restore' && restoreFrom && current && (
        <RestoreDialog
          doc={doc}
          from={restoreFrom}
          nextNumber={(versions[0]?.number ?? 0) + 1}
          onClose={() => setDialog(null)}
          onRestored={(next) => {
            setDialog(null)
            const added = next.currentVersion
            if (added) setViewing(added.number)
            applied(
              next,
              `v${restoreFrom.number} restored as v${added?.number}.${published ? ` Readers still see v${published.number} until v${added?.number} is approved.` : ''}`,
            )
          }}
        />
      )}
      {dialog === 'compare' && draft && published && (
        <CompareDialog documentId={doc.id} from={published} to={draft} onClose={() => setDialog(null)} />
      )}
      {dialog === 'delete' && (
        <DeleteDialog
          doc={doc}
          onClose={() => setDialog(null)}
          onDeleted={() => navigate(folderLink(doc.folder), { replace: true })}
        />
      )}
    </>
  )
}

/**
 * The version's PDF in the browser's own viewer, through a 5-minute signed link (every preview is
 * audited). Page and zoom go in the URL fragment, which doesn't fetch the file again.
 */
function PdfPreview({
  documentId,
  version,
  page,
  zoom,
  canDownload,
  onDownload,
}: {
  documentId: string
  version: VersionDto
  page: number
  zoom: number
  canDownload: boolean
  onDownload: () => void
}) {
  const isPdf = version.contentType === 'application/pdf'
  const link = useApi(
    () => (isPdf ? getContentLink(documentId, { versionId: version.id, inline: true }) : Promise.resolve(null)),
    [documentId, version.id, isPdf],
  )

  if (!isPdf) {
    return (
      <div className={styles.previewEmpty}>
        <FileWarning size={22} aria-hidden />
        <span>
          {version.fileName} can&apos;t be previewed in the browser ({version.contentType}).
        </span>
        {canDownload && (
          <Button size="sm" onClick={onDownload}>
            Download v{version.number}
          </Button>
        )}
      </div>
    )
  }
  if (link.error) return <ErrorState error={link.error} onRetry={link.reload} />
  if (!link.data || link.data.versionId !== version.id) return <Loading label={`Opening v${version.number}…`} />

  return (
    <iframe
      key={link.data.url}
      className={styles.pdf}
      title={`${version.fileName} · v${version.number}`}
      src={`${link.data.url}#page=${page}&zoom=${zoom}&toolbar=0&navpanes=0`}
    />
  )
}

/** Who can open the document. Everyone's effective access is only listed for people who manage it. */
function PermissionsPanel({ doc, isSystem, canManage }: { doc: DocumentDetail; isSystem: boolean; canManage: boolean }) {
  const access = useApi((signal) => (canManage ? getAccess('documents', doc.id, signal) : Promise.resolve(null)), [doc.id, canManage])
  const ownGrants = access.data?.grants.filter((g) => !g.expired).length ?? 0

  const intro = !doc.inheritPermissions
    ? `This document doesn't inherit the access of ${doc.folder.name}; only its own grants and the owner apply.`
    : isSystem
      ? 'This document is in a system folder, so access follows the org tree.'
      : `Access comes from the folder ${doc.folder.name}${ownGrants ? `, plus ${ownGrants} grant${ownGrants === 1 ? '' : 's'} on this document` : ''}.`

  return (
    <>
      <p className={styles.sideText}>{intro}</p>
      {!canManage ? (
        <>
          <ul className={styles.access}>
            <li>
              <User size={16} className={styles.accessIcon} aria-hidden />
              <span className={styles.accessWho}>
                <strong>You</strong>
                <span>Your access to this document</span>
              </span>
              <Badge tone={doc.myLevel === 'Manage' || doc.myLevel === 'Edit' ? 'info' : 'success'}>{levelLabel[doc.myLevel]}</Badge>
            </li>
          </ul>
          <p className={styles.sideNote}>Only people who manage this document see everyone&apos;s access.</p>
        </>
      ) : access.error ? (
        <ErrorState error={access.error} onRetry={access.reload} compact />
      ) : !access.data ? (
        <Loading compact label="Loading access…" />
      ) : (
        <ul className={styles.access}>
          {access.data.effective.map((e) => {
            const Icon = e.source.kind === 'owner' ? FolderUser : principalIcons[e.principal.type]
            const from =
              e.source.kind === 'owner'
                ? 'Document owner'
                : e.inherited
                  ? `Inherited · ${e.source.name}`
                  : 'This document'
            return (
              <li key={e.principal.key}>
                <Icon size={16} className={styles.accessIcon} aria-hidden />
                <span className={styles.accessWho}>
                  <strong>{e.principal.name}</strong>
                  <span>{e.principal.detail ? `${e.principal.detail} · ${from}` : from}</span>
                </span>
                <Badge tone={e.level === 'Manage' || e.level === 'Edit' ? 'info' : 'success'}>{levelLabel[e.level]}</Badge>
              </li>
            )
          })}
        </ul>
      )}
      <Link to={`/library/folders/${doc.folder.id}/access`} className={styles.accessLink}>
        <FolderOpen size={14} aria-hidden />
        {canManage && !isSystem ? 'Manage folder access' : 'See folder access'}
      </Link>
    </>
  )
}

/** What happened to the document: its versions and submissions, plus what you did here. */
function ActivityPanel({ doc, sessionLog, myId }: { doc: DocumentDetail; sessionLog: ActivityItem[]; myId: string }) {
  const items = useMemo<ActivityItem[]>(() => {
    const fromVersions = doc.versions.map((v) => ({
      id: v.id,
      text: v.createdFromVersionNumber
        ? `Restored v${v.createdFromVersionNumber} as v${v.number}`
        : v.number === 1
          ? 'Uploaded v1'
          : `Uploaded v${v.number}${v.changeDescription ? ` · ${v.changeDescription}` : ''}`,
      who: v.uploadedBy.id === myId ? 'You' : v.uploadedBy.displayName,
      when: shortDate(v.uploadedAt),
    }))
    const fromSubmissions = doc.submissions.map((s) => {
      const version = doc.versions.find((v) => v.id === s.documentVersionId)
      return {
        id: s.id,
        text: `v${version?.number ?? '?'} in submission ${s.number}`,
        who: 'Workflow',
        when: submissionLabel[s.status],
      }
    })
    return [...sessionLog, ...fromSubmissions, ...fromVersions]
  }, [doc, sessionLog, myId])

  return (
    <ul className={styles.activity}>
      {items.map((a) => (
        <li key={a.id}>
          <span className={styles.activityText}>{a.text}</span>
          <span className={styles.activityMeta}>
            {a.who} · {a.when}
          </span>
        </li>
      ))}
    </ul>
  )
}

function EditMetadataDialog({
  doc,
  onClose,
  onSaved,
  onStale,
}: {
  doc: DocumentDetail
  onClose: () => void
  onSaved: (doc: DocumentDetail) => void
  onStale: () => void
}) {
  const types = useApi(() => getDocumentTypes(), [])
  const years = useApi(() => getAcademicYears(), [])
  const [title, setTitle] = useState(doc.title)
  const [description, setDescription] = useState(doc.description ?? '')
  const [typeId, setTypeId] = useState(doc.type.id)
  const [yearId, setYearId] = useState(doc.period.academicYearId ?? '')
  const [termId, setTermId] = useState(doc.period.academicTermId ?? '')
  const [error, setError] = useState('')
  const save = useAction(updateDocument)

  // Academic-only types don't fit office folders and vice versa (the server has the final say for
  // library folders tied to a unit). A retired type stays selectable for the document that has it.
  const root = doc.breadcrumbs[0]?.kind
  const typeOptions = useMemo(() => {
    const fits = (scope: TypeScope) =>
      scope === 'All' || (root === 'AcademicRoot' ? scope === 'Academic' : root === 'OfficeRoot' ? scope === 'NonAcademic' : true)
    const list = (types.data ?? []).filter((t) => fits(t.scope)).map((t) => ({ id: t.id, code: t.code, name: t.name, tag: t.tag }))
    return list.some((t) => t.id === doc.type.id)
      ? list
      : [{ id: doc.type.id, code: doc.type.code, name: `${doc.type.name} (retired)`, tag: doc.type.academicTag }, ...list]
  }, [types.data, doc.type, root])
  const type = typeOptions.find((t) => t.id === typeId)
  const tag = type?.tag
  const terms = useMemo(() => (years.data ?? []).flatMap((y) => y.terms), [years.data])
  const yearOptions = years.data ?? []
  const chosenYear = yearId || yearOptions.find((y) => y.terms.some((t) => t.status === 'Active'))?.id || yearOptions[0]?.id || ''
  const chosenTerm = termId || terms.find((t) => t.status === 'Active')?.id || terms[0]?.id || ''

  const submit = async () => {
    if (!title.trim()) {
      setError('Give the document a title.')
      return
    }
    if (!type) {
      setError('Choose a document type. Every document needs one.')
      return
    }
    setError('')
    const term = terms.find((t) => t.id === chosenTerm)
    const period =
      tag === 'Term'
        ? { academicYearId: term?.academicYearId ?? null, academicTermId: chosenTerm || null }
        : tag === 'Year'
          ? { academicYearId: chosenYear || null, academicTermId: null }
          : { academicYearId: null, academicTermId: null }
    const next = await save.run(doc.id, doc.eTag, {
      title: title.trim(),
      description: description.trim(),
      documentTypeId: typeId === doc.type.id ? null : typeId,
      period,
      tags: null,
    })
    if (next) onSaved(next)
  }

  const stale = save.error instanceof ApiError && save.error.isStale

  return (
    <Modal
      title="Edit metadata"
      description="Changes apply to the document, not to a version."
      width={520}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          {stale ? (
            <Button
              variant="primary"
              onClick={() => {
                onStale()
                onClose()
              }}
            >
              Reload document
            </Button>
          ) : (
            <Button variant="primary" disabled={save.pending || !types.data || !years.data} onClick={submit}>
              {save.pending ? 'Saving…' : 'Save'}
            </Button>
          )}
        </>
      }
    >
      <label className={styles.field}>
        <span>Title</span>
        <input className="field" value={title} onChange={(e) => setTitle(e.target.value)} />
      </label>
      <div className={styles.field}>
        <span>Document number</span>
        <span className={styles.readonly}>{doc.documentNumber ?? 'Assigned from the type’s number pattern'}</span>
      </div>
      <label className={styles.field}>
        <span>Description</span>
        <textarea className="field" rows={2} value={description} onChange={(e) => setDescription(e.target.value)} />
      </label>
      {types.error || years.error ? (
        <ErrorState
          error={types.error ?? years.error}
          onRetry={() => {
            types.reload()
            years.reload()
          }}
          compact
        />
      ) : !types.data || !years.data ? (
        <Loading compact label="Loading types and terms…" />
      ) : (
        <>
          <label className={styles.field}>
            <span>Document type</span>
            <select className="field" value={typeId} onChange={(e) => setTypeId(e.target.value)}>
              {typeOptions.map((t) => (
                <option key={t.id} value={t.id}>
                  {typeLabel(t)}
                </option>
              ))}
            </select>
          </label>
          {tag === 'Term' && (
            <label className={styles.field}>
              <span>Academic term (required by this type)</span>
              <select className="field" value={chosenTerm} onChange={(e) => setTermId(e.target.value)}>
                {terms.map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.name} · {t.academicYearName}
                  </option>
                ))}
              </select>
            </label>
          )}
          {tag === 'Year' && (
            <label className={styles.field}>
              <span>Academic year (required by this type)</span>
              <select className="field" value={chosenYear} onChange={(e) => setYearId(e.target.value)}>
                {yearOptions.map((y) => (
                  <option key={y.id} value={y.id}>
                    {y.name}
                  </option>
                ))}
              </select>
            </label>
          )}
          {tag === 'None' && type && <p className={styles.dialogText}>{type.name} documents aren&apos;t tagged with a year or term.</p>}
        </>
      )}
      {error && <p className={styles.error}>{error}</p>}
      <InlineError error={save.error} />
    </Modal>
  )
}

function UploadVersionDialog({
  documentId,
  nextNumber,
  onClose,
  onUploaded,
}: {
  documentId: string
  nextNumber: number
  onClose: () => void
  onUploaded: (doc: DocumentDetail) => void
}) {
  const [file, setFile] = useState<File | null>(null)
  const [note, setNote] = useState('')
  const [error, setError] = useState('')
  const [progress, setProgress] = useState<number | null>(null)
  const upload = useAction(async (chosen: File, changeDescription: string) => {
    const uploadId = await uploadFile(chosen, {
      documentId,
      onProgress: ({ loaded, total }) => setProgress(total ? Math.round((loaded / total) * 100) : null),
    })
    return completeUpload(uploadId, {
      folderId: null,
      documentTypeId: null,
      title: null,
      description: null,
      academicYearId: null,
      academicTermId: null,
      tags: null,
      documentId,
      changeDescription,
    })
  })

  return (
    <Modal
      title={`Upload new version (v${nextNumber})`}
      description="Earlier versions stay exactly as they are. The new version starts as a private draft."
      width={500}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            disabled={upload.pending}
            onClick={async () => {
              if (!file) {
                setError('Choose the PDF to upload.')
                return
              }
              if (!note.trim()) {
                setError('Describe what changed in this version.')
                return
              }
              setError('')
              const next = await upload.run(file, note.trim())
              if (next) onUploaded(next)
            }}
          >
            {upload.pending ? (progress !== null && progress < 100 ? `Uploading ${progress}%…` : 'Filing…') : `Upload v${nextNumber}`}
          </Button>
        </>
      }
    >
      <label className={styles.field}>
        <span>PDF file</span>
        <input
          type="file"
          accept="application/pdf,.pdf"
          onChange={(e) => {
            setFile(e.target.files?.[0] ?? null)
            setError('')
          }}
        />
        {file && (
          <span className={styles.fileChosen}>
            {file.name} · {formatBytes(file.size)}
          </span>
        )}
      </label>
      <label className={styles.field}>
        <span>What changed?</span>
        <textarea
          className="field"
          rows={3}
          value={note}
          placeholder="e.g. Revised §5 sanctions wording"
          onChange={(e) => {
            setNote(e.target.value)
            setError('')
          }}
        />
      </label>
      {error && <p className={styles.error}>{error}</p>}
      <InlineError error={upload.error} />
    </Modal>
  )
}

function RestoreDialog({
  doc,
  from,
  nextNumber,
  onClose,
  onRestored,
}: {
  doc: DocumentDetail
  from: VersionDto
  nextNumber: number
  onClose: () => void
  onRestored: (doc: DocumentDetail) => void
}) {
  const restore = useAction(() => restoreVersion(doc.id, from.id))
  const published = doc.publishedVersion
  return (
    <Modal
      title={`Restore v${from.number} as v${nextNumber}?`}
      width={480}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="primary"
            icon={RotateCcw}
            disabled={restore.pending}
            onClick={async () => {
              const next = await restore.run()
              if (next) onRestored(next)
            }}
          >
            {restore.pending ? 'Restoring…' : 'Restore as new version'}
          </Button>
        </>
      }
    >
      <p className={styles.dialogText}>
        The bytes of v{from.number} are copied into a new version v{nextNumber} with the same SHA-256, and it is processed again. v
        {from.number} itself doesn&apos;t change.
      </p>
      <p className={styles.dialogText}>
        CurrentVersionId moves to v{nextNumber}.{' '}
        {published ? `PublishedVersionId stays on v${published.number} until the new version is approved and published.` : ''}
      </p>
      <InlineError error={restore.error} />
    </Modal>
  )
}

function CompareDialog({
  documentId,
  from,
  to,
  onClose,
}: {
  documentId: string
  from: VersionDto
  to: VersionDto
  onClose: () => void
}) {
  const comparison = useApi((signal) => compareVersions(documentId, from.number, to.number, signal), [documentId, from.number, to.number])
  const changes = comparison.data?.changes.filter((c) => c.field !== 'state') ?? []
  return (
    <Modal
      title={`Compare v${from.number} → v${to.number}`}
      description="What differs between the two versions' files and details. A text diff of the pages isn't available yet."
      width={640}
      onClose={onClose}
      footer={<Button onClick={onClose}>Close</Button>}
    >
      {comparison.error ? (
        <ErrorState error={comparison.error} onRetry={comparison.reload} compact />
      ) : !comparison.data ? (
        <Loading compact label="Comparing…" />
      ) : (
        <div className={styles.diff}>
          {changes.map((c) => (
            <div key={c.field} className={styles.diff}>
              <span className="section-label">{compareLabels[c.field] ?? c.field}</span>
              <p className={styles.diffRemoved}>− {compareValue(c, c.from)}</p>
              <p className={styles.diffAdded}>+ {compareValue(c, c.to)}</p>
            </div>
          ))}
          <p className={styles.diffSummary}>
            {comparison.data.sameContent
              ? 'Same file content (identical SHA-256).'
              : `Different file content · ${changes.length} detail${changes.length === 1 ? '' : 's'} changed`}
          </p>
        </div>
      )}
    </Modal>
  )
}

function DeleteDialog({ doc, onClose, onDeleted }: { doc: DocumentDetail; onClose: () => void; onDeleted: () => void }) {
  const remove = useAction(async () => {
    await deleteDocument(doc.id, doc.eTag)
    return true
  })
  return (
    <Modal
      title={`Delete ${doc.documentNumber ?? doc.title}?`}
      width={480}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button
            variant="danger"
            icon={Trash2}
            disabled={remove.pending}
            onClick={async () => {
              if (await remove.run()) onDeleted()
            }}
          >
            {remove.pending ? 'Deleting…' : 'Delete document'}
          </Button>
        </>
      }
    >
      <p className={styles.dialogText}>
        {doc.title} and its {doc.versions.length} version{doc.versions.length === 1 ? '' : 's'} leave the library and search. The audit
        trail keeps the record, and retention rules still apply.
      </p>
      <InlineError error={remove.error} />
    </Modal>
  )
}

import { Fragment, useEffect, useState } from 'react'
import { ArrowRight, RefreshCw, X } from 'lucide-react'
import { Link } from 'react-router'
import { getIndexingStatus, listProcessingJobs, rebuildIndex, retryFailedJobs, retryProcessingJob } from '../../api/admin'
import type { ProcessingJobQuery } from '../../api/admin'
import type { IndexBuildRow, IndexingStatus, JobStatus, PagedResultOfProcessingJobRow, ProcessingJobRow, ProcessingStage } from '../../api/types'
import { useAction, useApi } from '../../api/useApi'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Badge } from '../../components/Badge'
import type { BadgeTone } from '../../components/Badge'
import { Button } from '../../components/Button'
import { Modal } from '../../components/Modal'
import { PageHeader } from '../../components/PageHeader'
import { dateTime, formatBytes } from '../../lib/format'
import styles from './ProcessingPage.module.css'

type JobFilter = 'all' | 'attention' | 'running' | 'queued'

/** A box in the pipeline strip; `stage` is the API's StageCount name. */
type StripStage = 'Queued' | ProcessingStage

const n = (v: number) => v.toLocaleString('en-US')
const POLL_MS = 3000
const PAGE_SIZE = 50

// The strip the API reports: Queued, the running count per stage, then Done (succeeded jobs).
const pipelineStages: { stage: StripStage; label: string; tone: 'neutral' | 'purple' | 'warning' | 'orange' | 'success'; ai?: boolean }[] = [
  { stage: 'Queued', label: 'Queued', tone: 'neutral' },
  { stage: 'Extract', label: 'Extract', tone: 'purple' },
  { stage: 'Ocr', label: 'OCR', tone: 'warning' },
  { stage: 'Chunk', label: 'Chunk', tone: 'purple', ai: true },
  { stage: 'Embed', label: 'Embed', tone: 'orange', ai: true },
  { stage: 'Index', label: 'Index', tone: 'orange', ai: true },
  { stage: 'Done', label: 'Done', tone: 'success' },
]

const stageName = (stage: ProcessingStage) => (stage === 'Ocr' ? 'OCR' : stage)

const isFailed = (s: JobStatus) => s === 'Failed' || s === 'DeadLettered'

function statusBadge(job: ProcessingJobRow): { tone: BadgeTone; label: string } {
  switch (job.status) {
    case 'DeadLettered':
      return { tone: 'danger', label: 'Dead-lettered' }
    case 'Failed': {
      if (!job.nextAttemptAt) return { tone: 'warning', label: 'Failed' }
      const minutes = Math.ceil((new Date(job.nextAttemptAt).getTime() - Date.now()) / 60_000)
      return { tone: 'warning', label: minutes > 0 ? `Retry in ${minutes} min` : 'Retrying soon' }
    }
    case 'Running':
      return { tone: 'info', label: 'Running' }
    case 'Queued':
      return { tone: 'neutral', label: 'Queued' }
    case 'Succeeded':
      return { tone: 'success', label: 'Done' }
  }
}

/** The jobs request for a filter chip or pipeline box. "Needs attention" is two statuses, so two requests. */
async function loadJobs(filter: JobFilter, stage: StripStage | null, signal: AbortSignal): Promise<PagedResultOfProcessingJobRow> {
  const base: ProcessingJobQuery = { pageSize: PAGE_SIZE }
  if (stage === 'Queued') base.status = 'Queued'
  else if (stage === 'Done') base.status = 'Succeeded'
  else if (stage) base.stage = stage

  const wanted: JobStatus[] | null =
    filter === 'attention' ? ['DeadLettered', 'Failed'] : filter === 'running' ? ['Running'] : filter === 'queued' ? ['Queued'] : null
  // A status box and a status chip together show only their overlap.
  const statuses = wanted ? wanted.filter((s) => !base.status || s === base.status) : [base.status]
  const pages = await Promise.all(statuses.map((status) => listProcessingJobs({ ...base, status }, signal)))
  return {
    items: pages.flatMap((p) => p.items),
    page: 1,
    pageSize: PAGE_SIZE,
    total: pages.reduce((sum, p) => sum + p.total, 0),
  }
}

export function ProcessingPage() {
  const [filter, setFilter] = useState<JobFilter>('all')
  const [stageFilter, setStageFilter] = useState<StripStage | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [details, setDetails] = useState<ProcessingJobRow | null>(null)
  const [rebuildOpen, setRebuildOpen] = useState(false)

  const status = useApi((signal) => getIndexingStatus(signal), [])
  const jobs = useApi((signal) => loadJobs(filter, stageFilter, signal), [filter, stageFilter])
  const retryOne = useAction(retryProcessingJob)
  const retryAll = useAction(retryFailedJobs)

  const byStatus = status.data?.jobsByStatus ?? {}
  const count = (s: JobStatus) => byStatus[s] ?? 0
  const failedCount = count('Failed') + count('DeadLettered')
  const busy = count('Queued') + count('Running') > 0
  const reloadStatus = status.reload
  const reloadJobs = jobs.reload

  // Live status while work is in flight: poll the strip and the list.
  useEffect(() => {
    if (!busy) return
    const id = window.setInterval(() => {
      reloadStatus()
      reloadJobs()
    }, POLL_MS)
    return () => window.clearInterval(id)
  }, [busy, reloadStatus, reloadJobs])

  useEffect(() => {
    if (!notice) return
    const id = window.setTimeout(() => setNotice(null), 3500)
    return () => window.clearTimeout(id)
  }, [notice])

  const refresh = () => {
    status.reload()
    jobs.reload()
  }

  const retry = async (job: ProcessingJobRow) => {
    if (await retryOne.run(job.id)) {
      setNotice('Requeued from the first stage.')
      setDetails(null)
      refresh()
    }
  }

  const retryFailed = async () => {
    const result = await retryAll.run()
    if (result) {
      setNotice(result.requeued === 1 ? 'Requeued 1 job.' : `Requeued ${n(result.requeued)} jobs.`)
      refresh()
    }
  }

  if (!status.data) {
    return (
      <>
        <PageHeader breadcrumb="Administration / Processing & index" title="Processing & search index" />
        {status.error ? <ErrorState error={status.error} onRetry={status.reload} /> : <Loading label="Loading processing status…" />}
      </>
    )
  }

  const { corpus, index } = status.data
  const stageCount = (stage: StripStage) => status.data?.stages.find((s) => s.stage === stage)?.count ?? 0

  const kpis = [
    { label: 'Documents', value: n(corpus.documents) },
    { label: 'Versions', value: n(corpus.versions) },
    { label: 'PDF pages', value: n(corpus.pages) },
    { label: 'Object storage', value: formatBytes(corpus.storageBytes) },
    {
      label: 'Vectors',
      value: index.activeBuild ? n(index.chunks) : 'Off',
      title: index.activeBuild
        ? `${n(index.chunks)} chunks from ${n(index.indexedVersions)} versions · ${n(index.tokens)} tokens`
        : (index.reason ?? undefined),
    },
  ]

  const filterCounts: Record<JobFilter, number> = {
    all: Object.values(byStatus).reduce((sum, c) => sum + c, 0),
    attention: failedCount,
    running: count('Running'),
    queued: count('Queued'),
  }

  const visible = jobs.data?.items ?? []
  const actionError = retryOne.error ?? retryAll.error

  return (
    <>
      <PageHeader
        breadcrumb="Administration / Processing & index"
        title="Processing & search index"
        actions={
          <>
            <Button icon={RefreshCw} onClick={() => void retryFailed()} disabled={failedCount === 0 || retryAll.pending}>
              {retryAll.pending ? 'Requeuing…' : `Retry all failed (${n(failedCount)})`}
            </Button>
            <Button variant="primary" onClick={() => setRebuildOpen(true)}>
              Rebuild index…
            </Button>
          </>
        }
      />
      {status.error ? <ErrorState compact error={status.error} onRetry={status.reload} /> : null}

      <div className={styles.kpis}>
        {kpis.map((k) => (
          <div key={k.label} className={styles.kpi} title={k.title}>
            <span className={styles.kpiLabel}>{k.label}</span>
            <span className={styles.kpiValue}>{k.value}</span>
          </div>
        ))}
        <button
          type="button"
          className={`${styles.kpi} ${styles.kpiDanger}`}
          onClick={() => {
            setFilter('attention')
            setStageFilter(null)
          }}
          title="Show failed and dead-lettered jobs"
        >
          <span className={styles.kpiLabel}>Failed / dead-lettered</span>
          <span className={styles.kpiValue}>{n(failedCount)}</span>
        </button>
      </div>

      <section className={`${styles.card} ${styles.pipeline}`} aria-label="Pipeline">
        <span className={styles.eyebrow}>Pipeline — jobs per stage right now (Done = all finished jobs)</span>
        <div className={styles.stages}>
          {pipelineStages.map((s, i) => (
            <Fragment key={s.stage}>
              {i > 0 && <ArrowRight size={16} aria-hidden style={{ flexShrink: 0 }} />}
              <button
                type="button"
                className={`${styles.stage} ${styles[s.tone]} ${stageFilter === s.stage ? styles.stageActive : ''}`}
                aria-pressed={stageFilter === s.stage}
                title={s.ai && !index.aiEnabled ? `Skipped while AI is off · show jobs at ${s.label}` : `Show jobs at ${s.label}`}
                onClick={() => setStageFilter((cur) => (cur === s.stage ? null : s.stage))}
              >
                <span className={styles.stageLabel}>
                  {s.label}
                  {s.ai && !index.aiEnabled ? ' (AI off)' : ''}
                </span>
                <span className={styles.stageValue}>{n(stageCount(s.stage))}</span>
              </button>
            </Fragment>
          ))}
        </div>
        <span className={styles.workers}>
          Pipeline v{status.data.pipelineVersion} · up to {status.data.maxAttempts} attempts per job ·{' '}
          {status.data.outboxBacklog === 0
            ? 'no uploads waiting to be queued'
            : `${n(status.data.outboxBacklog)} upload${status.data.outboxBacklog === 1 ? '' : 's'} waiting to be queued`}
          {busy ? ' · refreshing every 3 s' : ''}
        </span>
      </section>

      <div className={styles.body}>
        <section className={`${styles.card} ${styles.jobs}`} aria-label="Processing jobs">
          <div className={styles.toolbar}>
            {(
              [
                ['all', 'All jobs'],
                ['attention', 'Needs attention'],
                ['running', 'Running'],
                ['queued', 'Queued'],
              ] as const
            ).map(([key, label]) => (
              <button
                key={key}
                type="button"
                className={`${styles.filter} ${filter === key ? styles.filterOn : ''}`}
                aria-pressed={filter === key}
                onClick={() => setFilter(key)}
              >
                {label} <span className={styles.filterCount}>{n(filterCounts[key])}</span>
              </button>
            ))}
            {stageFilter && (
              <button type="button" className={`${styles.filter} ${styles.filterOn}`} onClick={() => setStageFilter(null)}>
                Stage: {pipelineStages.find((s) => s.stage === stageFilter)?.label}
                <X size={12} aria-label="Clear stage filter" />
              </button>
            )}
            {notice && <span className={styles.notice}>{notice}</span>}
          </div>
          {actionError ? (
            <div className={styles.actionError}>
              <InlineError error={actionError} />
            </div>
          ) : null}

          <div className={styles.headRow}>
            <span className={`${styles.th} ${styles.fill}`}>Document · version</span>
            <span className={`${styles.th} ${styles.colStage}`}>Stage</span>
            <span className={`${styles.th} ${styles.colTries}`}>Tries</span>
            <span className={`${styles.th} ${styles.colStatus}`}>Status</span>
            <span className={`${styles.th} ${styles.colError}`}>Last error</span>
            <span className={styles.colAction} />
          </div>

          {jobs.error && !jobs.data ? <ErrorState compact error={jobs.error} onRetry={jobs.reload} /> : null}
          {!jobs.data && !jobs.error ? <Loading compact label="Loading jobs…" /> : null}

          {visible.map((j) => {
            const badge = statusBadge(j)
            return (
              <div key={j.id} className={styles.row}>
                <Link to={`/documents/${j.documentId}`} className={styles.name} title={j.fileName}>
                  {j.title} · v{j.version}
                </Link>
                <span className={styles.colStage}>{j.status === 'Queued' ? '—' : stageName(j.stage)}</span>
                <span className={styles.colTries}>
                  {j.attempt}/{j.maxAttempts}
                </span>
                <span className={styles.colStatus}>
                  <Badge tone={badge.tone}>{badge.label}</Badge>
                </span>
                <span className={styles.colError} title={j.lastError ?? undefined}>
                  {j.lastError ?? '—'}
                </span>
                <span className={styles.colAction}>
                  {isFailed(j.status) ? (
                    <button type="button" className={styles.action} onClick={() => void retry(j)} disabled={retryOne.pending}>
                      {j.status === 'Failed' ? 'Retry now' : 'Retry'}
                    </button>
                  ) : (
                    <button type="button" className={styles.action} onClick={() => setDetails(j)}>
                      Details
                    </button>
                  )}
                </span>
              </div>
            )
          })}
          {jobs.data && visible.length === 0 && (
            <div className={styles.empty}>
              {filter === 'all' && !stageFilter ? 'No processing jobs yet. Uploading a file queues one.' : 'No jobs match this filter.'}
            </div>
          )}
          {jobs.data && jobs.data.total > visible.length && (
            <div className={styles.empty}>
              Showing {n(visible.length)} of {n(jobs.data.total)} jobs, problems first.
            </div>
          )}
        </section>

        <IndexPanel status={status.data} />
      </div>

      {details && (
        <JobDetails
          job={details}
          retrying={retryOne.pending}
          error={retryOne.error}
          onClose={() => setDetails(null)}
          onRetry={() => void retry(details)}
        />
      )}
      {rebuildOpen && (
        <RebuildModal
          status={status.data}
          onClose={() => setRebuildOpen(false)}
          onStarted={() => {
            setRebuildOpen(false)
            status.reload()
          }}
        />
      )}
    </>
  )
}

const buildPercent = (build: IndexBuildRow) => Math.floor(build.progress <= 1 ? build.progress * 100 : build.progress)

function IndexPanel({ status }: { status: IndexingStatus }) {
  const { index } = status
  const build = index.lastBuild
  const active = index.activeBuild
  const inFlight = build && (build.status === 'Pending' || build.status === 'Building')
  return (
    <aside className={`${styles.card} ${styles.index}`} aria-label="Search index">
      <span className={styles.eyebrow}>Search index</span>
      <div className={styles.kv}>
        <span className={styles.kvKey}>Keyword search</span>
        <span className={`${styles.kvValue} ${styles.kvGood}`}>On</span>
      </div>
      <span className={styles.help}>{index.keywordSearch}</span>
      <div className={styles.kv}>
        <span className={styles.kvKey}>Semantic search</span>
        <span className={`${styles.kvValue} ${index.semanticEnabled ? styles.kvGood : ''}`}>
          {index.semanticEnabled ? 'On' : index.aiEnabled ? 'Not ready' : 'Off'}
        </span>
      </div>
      {!index.semanticEnabled && index.reason && <span className={styles.help}>{index.reason}</span>}
      <div className={styles.kv}>
        <span className={styles.kvKey}>Alias</span>
        <span className={styles.kvValue}>
          {index.collectionAlias}
          {active ? ` → ${active.collection}` : ' · not built'}
        </span>
      </div>
      {active && (
        <div className={styles.kv}>
          <span className={styles.kvKey}>Indexed</span>
          <span className={styles.kvValue}>
            {n(index.indexedVersions)} versions · {n(index.chunks)} chunks
          </span>
        </div>
      )}
      {active && index.pendingVersions > 0 && (
        <div className={styles.kv}>
          <span className={styles.kvKey}>Waiting</span>
          <span className={styles.kvValue}>{n(index.pendingVersions)} versions</span>
        </div>
      )}
      <div className={styles.kv}>
        <span className={styles.kvKey}>Embedding</span>
        <span className={styles.kvValue}>
          {index.embeddingModel ? `${index.embeddingModel} · ${n(index.embeddingDimensions)} dims` : 'None configured'}
        </span>
      </div>
      {index.chatModel && (
        <div className={styles.kv}>
          <span className={styles.kvKey}>Answers</span>
          <span className={styles.kvValue}>
            {index.chatModel} · {index.chatProvider === 'Anthropic' ? 'Anthropic API' : 'local model'}
          </span>
        </div>
      )}
      <div className={styles.kv}>
        <span className={styles.kvKey}>Pipeline</span>
        <span className={styles.kvValue}>v{status.pipelineVersion}</span>
      </div>
      {build && !inFlight && (
        <div className={styles.kv}>
          <span className={styles.kvKey}>Last build</span>
          <span className={styles.kvValue}>
            {build.status} · {dateTime(build.completedAt ?? build.createdAt)}
          </span>
        </div>
      )}
      {build && inFlight && (
        <div className={styles.rebuild}>
          <span className={styles.rebuildTitle}>
            {active ? 'Rebuild running' : 'First build running'} → {build.collection}
          </span>
          <div className={styles.track} role="progressbar" aria-valuenow={buildPercent(build)} aria-valuemin={0} aria-valuemax={100}>
            <div className={styles.bar} style={{ width: `${buildPercent(build)}%` }} />
          </div>
          <span>
            {buildPercent(build)}% · {n(build.indexedVersions)} of {n(build.totalVersions)} versions · {build.model}.{' '}
            {active ? 'Search keeps using the current collection until the alias swaps.' : 'Semantic search starts when it finishes.'}
          </span>
          {build.lastError && <span className={styles.warn}>Paused: {build.lastError}</span>}
        </div>
      )}
      {build?.status === 'Failed' && build.lastError && <span className={styles.warn}>{build.lastError}</span>}
    </aside>
  )
}

/** The job's per-stage notes (metrics JSON), e.g. "ocr: skipped: OCR engine not configured". */
function metricLines(metrics: unknown) {
  if (!metrics || typeof metrics !== 'object' || Array.isArray(metrics)) return []
  const show = (value: unknown): string => {
    if (typeof value === 'string') return value
    if (value && typeof value === 'object' && !Array.isArray(value)) {
      return Object.entries(value)
        .map(([k, v]) => `${k}: ${show(v)}`)
        .join(' · ')
    }
    return JSON.stringify(value)
  }
  return Object.entries(metrics as Record<string, unknown>).map(([key, value]) => ({ key, value: show(value) }))
}

interface JobDetailsProps {
  job: ProcessingJobRow
  retrying: boolean
  error: unknown
  onClose: () => void
  onRetry: () => void
}

function JobDetails({ job, retrying, error, onClose, onRetry }: JobDetailsProps) {
  const metrics = metricLines(job.metrics)
  return (
    <Modal
      title={`${job.title} · v${job.version}`}
      description={job.fileName}
      onClose={onClose}
      footer={
        <>
          {isFailed(job.status) && (
            <Button onClick={onRetry} disabled={retrying}>
              {retrying ? 'Requeuing…' : 'Retry'}
            </Button>
          )}
          <Button variant="primary" onClick={onClose}>
            Close
          </Button>
        </>
      }
    >
      <div className={styles.detailGrid}>
        <span className={styles.detailKey}>Status</span>
        <span>{statusBadge(job).label}</span>
        <span className={styles.detailKey}>Stage</span>
        <span>{stageName(job.stage)}</span>
        <span className={styles.detailKey}>Attempts</span>
        <span>
          {job.attempt} of {job.maxAttempts}
        </span>
        <span className={styles.detailKey}>Updated</span>
        <span>{dateTime(job.updatedAt)}</span>
        {job.lastError && (
          <>
            <span className={styles.detailKey}>Last error</span>
            <span>{job.lastError}</span>
          </>
        )}
        {job.owner && (
          <>
            <span className={styles.detailKey}>Owner</span>
            <span>{job.owner}</span>
          </>
        )}
        {metrics.map((m) => (
          <Fragment key={m.key}>
            <span className={styles.detailKey}>{m.key}</span>
            <span>{m.value}</span>
          </Fragment>
        ))}
        <span className={styles.detailKey}>Document</span>
        <Link to={`/documents/${job.documentId}`}>Open document</Link>
      </div>
      {job.status === 'DeadLettered' && (
        <p className={styles.warn}>
          Dead-lettered jobs aren’t retried automatically. If the file itself is the problem, ask the owner to upload a
          fixed copy as a new version instead of retrying.
        </p>
      )}
      <InlineError error={error} />
    </Modal>
  )
}

function RebuildModal({ status, onClose, onStarted }: { status: IndexingStatus; onClose: () => void; onStarted: () => void }) {
  const { index, corpus } = status
  const start = useAction(() => rebuildIndex(index.embeddingModel ?? undefined))
  const run = async () => {
    if (await start.run()) onStarted()
  }

  return (
    <Modal
      title="Rebuild search index"
      description={`Creates a new ${index.collectionAlias} collection and re-indexes every eligible version. Search stays up the whole time.`}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Close</Button>
          <Button variant="primary" onClick={() => void run()} disabled={start.pending}>
            {start.pending ? 'Starting…' : 'Start rebuild'}
          </Button>
        </>
      }
    >
      <div className={styles.detailGrid}>
        <span className={styles.detailKey}>Embedding model</span>
        <span>
          {index.embeddingModel ? `${index.embeddingModel} · ${n(index.embeddingDimensions)} dims (configured)` : 'None configured'}
        </span>
        <span className={styles.detailKey}>Versions</span>
        <span>{n(corpus.versions)}</span>
      </div>
      {!index.aiEnabled && (
        <p className={styles.warn}>
          Semantic search is off{index.reason ? `: ${index.reason}` : '.'} Keyword search doesn’t need a rebuild.
        </p>
      )}
      <InlineError error={start.error} />
    </Modal>
  )
}

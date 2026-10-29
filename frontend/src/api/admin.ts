import { api } from './client'
import type {
  IndexBuildRow,
  IndexingStatus,
  JobStatus,
  PagedResultOfAuditRow,
  PagedResultOfProcessingJobRow,
  ProcessingJobRow,
  ProcessingStage,
  RetryResult,
} from './types'

// Processing jobs, indexing status and the audit log (backend AdminEndpoints, frames 6, 10). Admin only.

export interface ProcessingJobQuery {
  status?: JobStatus
  stage?: ProcessingStage
  q?: string
  page?: number
  pageSize?: number
}

export interface AuditQuery {
  actorUserId?: string
  /** "document." matches every action with that prefix. */
  action?: string
  resourceType?: string
  resourceId?: string
  from?: string
  to?: string
  page?: number
  pageSize?: number
}

/** Problems first. */
export const listProcessingJobs = (query: ProcessingJobQuery = {}, signal?: AbortSignal) =>
  api.get<PagedResultOfProcessingJobRow>('/admin/processing-jobs', { query: { ...query }, signal })

/** Pipeline stage counts, corpus totals and search index state. */
export const getIndexingStatus = (signal?: AbortSignal) => api.get<IndexingStatus>('/admin/indexing-status', { signal })

/** Re-queues a failed or dead-lettered job from the first stage. */
export const retryProcessingJob = (id: string) => api.post<ProcessingJobRow>(`/admin/processing-jobs/${id}/retry`)

export const retryFailedJobs = () => api.post<RetryResult>('/admin/processing-jobs/retry-failed')

/** Queues a build into a new collection; the index worker fills it and swaps the alias (409 if one is running, 501 with AI off). */
export const rebuildIndex = (model?: string) => api.post<IndexBuildRow>('/admin/indexes/rebuild', { model: model ?? null })

/** Newest first. */
export const listAuditEvents = (query: AuditQuery = {}, signal?: AbortSignal) =>
  api.get<PagedResultOfAuditRow>('/admin/audit', { query: { ...query }, signal })

export const listAuditActions = (signal?: AbortSignal) => api.get<string[]>('/admin/audit/actions', { signal })

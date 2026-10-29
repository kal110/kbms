import { api } from './client'
import type {
  AddAttachmentRequest,
  CreateSubmissionRequest,
  DecisionRequest,
  NotificationSummary,
  PagedResultOfNotificationItem,
  PagedResultOfReviewQueueItem,
  PagedResultOfSubmissionRow,
  ReassignRequest,
  SubmissionDashboard,
  SubmissionDetail,
  SubmissionStatus,
  TimelineEntry,
} from './types'

// Submissions, reviews, the college overview and notifications (backend WorkflowEndpoints).
// Changes take the submission's numeric `version` as If-Match; decisions and submit/resubmit send an
// Idempotency-Key so a retried click can't act twice.

export type SubmissionScope = 'mine' | 'reviewing' | 'unit' | 'college'

export interface SubmissionListQuery {
  scope?: SubmissionScope
  status?: SubmissionStatus
  termId?: string
  typeId?: string
  collegeId?: string
  q?: string
  page?: number
  pageSize?: number
}

const once = { idempotencyKey: true } as const

export const listSubmissions = (query: SubmissionListQuery = {}, signal?: AbortSignal) =>
  api.get<PagedResultOfSubmissionRow>('/submissions', { query: { ...query }, signal })

/** Creates a draft against the effective checklist. */
export const createSubmission = (body: CreateSubmissionRequest) => api.post<SubmissionDetail>('/submissions', body)

export const getSubmission = (id: string, signal?: AbortSignal) => api.get<SubmissionDetail>(`/submissions/${id}`, { signal })

export const renameSubmission = (id: string, version: number, title: string) =>
  api.patch<SubmissionDetail>(`/submissions/${id}`, { title }, { ifMatch: version })

export const deleteDraft = (id: string, version: number) => api.delete(`/submissions/${id}`, { ifMatch: version })

/** Append-only history, oldest first. */
export const getTimeline = (id: string, signal?: AbortSignal) =>
  api.get<TimelineEntry[]>(`/submissions/${id}/timeline`, { signal })

/** Attaches an uploaded file (see uploadFile in ./uploads) to a checklist slot, or adds a new version. */
export const addAttachment = (id: string, version: number, body: AddAttachmentRequest) =>
  api.post<SubmissionDetail>(`/submissions/${id}/attachments`, body, { ifMatch: version })

export const removeAttachment = (id: string, version: number, attachmentId: string) =>
  api.delete<SubmissionDetail>(`/submissions/${id}/attachments/${attachmentId}`, { ifMatch: version })

export const submitSubmission = (id: string, version: number) =>
  api.post<SubmissionDetail>(`/submissions/${id}/submit`, undefined, { ...once, ifMatch: version })

export const resubmitSubmission = (id: string, version: number, comments?: string) =>
  api.post<SubmissionDetail>(`/submissions/${id}/resubmit`, { comments: comments ?? null }, { ...once, ifMatch: version })

export const withdrawSubmission = (id: string, version: number) =>
  api.post<SubmissionDetail>(`/submissions/${id}/withdraw`, undefined, { ifMatch: version })

// ---- Reviewing

/** Submissions waiting for me, longest-waiting first. */
export const getPendingReviews = (page = 1, pageSize = 25, signal?: AbortSignal) =>
  api.get<PagedResultOfReviewQueueItem>('/reviews/pending', { query: { page, pageSize }, signal })

/** Starts reviewing (Submitted/Resubmitted → UnderReview). */
export const openReview = (id: string) => api.post<SubmissionDetail>(`/submissions/${id}/open`)

export const approveSubmission = (id: string, version: number, body: DecisionRequest) =>
  api.post<SubmissionDetail>(`/submissions/${id}/approve`, body, { ...once, ifMatch: version })

/** A comment is required. */
export const requestRevision = (id: string, version: number, body: DecisionRequest) =>
  api.post<SubmissionDetail>(`/submissions/${id}/request-revision`, body, { ...once, ifMatch: version })

/** An explanation is required; rejected submissions are never published. */
export const rejectSubmission = (id: string, version: number, body: DecisionRequest) =>
  api.post<SubmissionDetail>(`/submissions/${id}/reject`, body, { ...once, ifMatch: version })

/** Admin or the college Dean hands the current stage to another reviewer. */
export const reassignReview = (id: string, version: number, body: ReassignRequest) =>
  api.post<SubmissionDetail>(`/submissions/${id}/reassign`, body, { ifMatch: version })

/** Submissions dashboard: all colleges (VPAF, admin), a college (Dean) or a program (AS); ids drill in. */
export const getSubmissionDashboard = (
  scope: { collegeId?: string; programId?: string; departmentId?: string; nonAcademic?: boolean; termId?: string },
  signal?: AbortSignal,
) => api.get<SubmissionDashboard>('/dashboard/submissions', { query: scope, signal })

// ---- Notifications

export const listNotifications = (options: { unread?: boolean; page?: number; pageSize?: number } = {}, signal?: AbortSignal) =>
  api.get<PagedResultOfNotificationItem>('/notifications', { query: { ...options }, signal })

export const getNotificationSummary = (signal?: AbortSignal) =>
  api.get<NotificationSummary>('/notifications/summary', { signal })

export const markNotificationRead = (id: string) => api.post<void>(`/notifications/${id}/read`)

export const markAllNotificationsRead = () => api.post<{ updated: number }>('/notifications/read-all')

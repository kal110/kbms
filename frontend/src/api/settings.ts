import { invalidate } from './cache'
import { api } from './client'
import type {
  AcademicTermDto,
  AcademicYearDto,
  CreateDocumentTypeRequest,
  CreateSubmissionTypeRequest,
  DocumentTypeAdminDto,
  RetentionPolicyDto,
  SubmissionTypeAdminDto,
  UpdateAcademicTermRequest,
  UpdateDocumentTypeRequest,
  UpdateSubmissionTypeRequest,
} from './types'

// Admin settings: document types, the academic calendar and submission types (backend
// SettingsEndpoints, frames 17, 19, 20). Each change drops the cached reference lists so pickers
// elsewhere see it.

const changed =
  (...prefixes: string[]) =>
  <T>(result: T) => {
    prefixes.forEach(invalidate)
    return result
  }

// ---- Document types (frame 17)

export const listDocumentTypesForAdmin = (signal?: AbortSignal) =>
  api.get<DocumentTypeAdminDto[]>('/settings/document-types', { signal })

export const getDocumentTypeForAdmin = (id: string, signal?: AbortSignal) =>
  api.get<DocumentTypeAdminDto>(`/settings/document-types/${id}`, { signal })

export const createDocumentType = (body: CreateDocumentTypeRequest) =>
  api.post<DocumentTypeAdminDto>('/settings/document-types', body).then(changed('document-types'))

/** Null leaves a field unchanged; "" clears description or number pattern. Pass the row's `etag`. */
export const updateDocumentType = (id: string, etag: string, body: UpdateDocumentTypeRequest) =>
  api.patch<DocumentTypeAdminDto>(`/settings/document-types/${id}`, body, { ifMatch: etag }).then(changed('document-types'))

export const retireDocumentType = (id: string, etag: string, replacedByTypeId?: string | null) =>
  api
    .post<DocumentTypeAdminDto>(`/settings/document-types/${id}/retire`, { replacedByTypeId: replacedByTypeId ?? null }, { ifMatch: etag })
    .then(changed('document-types'))

export const reactivateDocumentType = (id: string, etag: string) =>
  api.post<DocumentTypeAdminDto>(`/settings/document-types/${id}/reactivate`, undefined, { ifMatch: etag }).then(changed('document-types'))

export const listRetentionPolicies = (signal?: AbortSignal) =>
  api.get<RetentionPolicyDto[]>('/settings/retention-policies', { signal })

// ---- Academic calendar (frame 19)

/** Years and terms with document, submission and requirement-set counts. */
export const listAcademicTermsForAdmin = (signal?: AbortSignal) => api.get<AcademicYearDto[]>('/settings/academic-terms', { signal })

export const updateAcademicTerm = (id: string, etag: string, body: UpdateAcademicTermRequest) =>
  api.patch<AcademicTermDto>(`/settings/academic-terms/${id}`, body, { ifMatch: etag }).then(changed('academic-years', 'active-term'))

/** Makes a planned term active (closes the current active term). */
export const activateAcademicTerm = (id: string, etag: string) =>
  api.post<AcademicTermDto>(`/settings/academic-terms/${id}/activate`, undefined, { ifMatch: etag }).then(changed('academic-years', 'active-term'))

export const closeAcademicTerm = (id: string, etag: string) =>
  api.post<AcademicTermDto>(`/settings/academic-terms/${id}/close`, undefined, { ifMatch: etag }).then(changed('academic-years', 'active-term'))

/** Not enabled yet: answers 501. */
export const syncCalendarFromSis = () => api.post<void>('/settings/academic-terms/sync')

// ---- Submission types (frame 20)

/** Every type, active first, with the submissions and published lists that use it. */
export const listSubmissionTypesForAdmin = (signal?: AbortSignal) =>
  api.get<SubmissionTypeAdminDto[]>('/settings/submission-types', { signal })

export const createSubmissionType = (body: CreateSubmissionTypeRequest) =>
  api.post<SubmissionTypeAdminDto>('/settings/submission-types', body).then(changed('submission-types'))

/** Null leaves a field unchanged. Stages are replaced in order; open submissions keep their stage number. */
export const updateSubmissionType = (id: string, body: UpdateSubmissionTypeRequest) =>
  api.patch<SubmissionTypeAdminDto>(`/settings/submission-types/${id}`, body).then(changed('submission-types'))

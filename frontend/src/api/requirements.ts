import { api } from './client'
import type {
  CreateRequirementSetRequest,
  EffectiveChecklistDto,
  RequirementScope,
  RequirementScopeTreeDto,
  RequirementSetDto,
  ScopeRequirementSetsDto,
  UpdateRequirementSetRequest,
} from './types'

// Required-document lists per college, department and program, and the effective checklist
// (backend RequirementsEndpoints, frames 18, 20, 23, 24).

const scopePath = (scope: RequirementScope) => scope.toLowerCase()

/** A scope's lists: published, draft and inherited slots, per submission type. */
export const getScopeRequirementSets = (scope: RequirementScope, scopeId: string, signal?: AbortSignal) =>
  api.get<ScopeRequirementSetsDto>(`/scopes/${scopePath(scope)}/${scopeId}/requirement-sets`, { signal })

/** Starts a draft (copies the published list, or `copyFromSetId`). */
export const createRequirementDraft = (scope: RequirementScope, scopeId: string, body: CreateRequirementSetRequest) =>
  api.post<RequirementSetDto>(`/scopes/${scopePath(scope)}/${scopeId}/requirement-sets`, body)

/** The scopes the caller can set required documents for. */
export const getRequirementScopeTree = (signal?: AbortSignal) =>
  api.get<RequirementScopeTreeDto>('/requirement-scopes/tree', { signal })

/** The caller's checklist for a submission type and term (default: the active term); programId picks one of their programs. */
export const getEffectiveChecklist = (
  query: { submissionTypeId: string; termId?: string; userId?: string; programId?: string; departmentId?: string },
  signal?: AbortSignal,
) => api.get<EffectiveChecklistDto>('/requirement-sets/effective', { query: { ...query }, signal })

export const getCopySources = (submissionTypeId: string, signal?: AbortSignal) =>
  api.get<RequirementSetDto[]>('/requirement-sets/copy-sources', { query: { submissionTypeId }, signal })

export const getRequirementSet = (id: string, signal?: AbortSignal) =>
  api.get<RequirementSetDto>(`/requirement-sets/${id}`, { signal })

/** Replaces a draft's notes, effective term and slots. Pass the set's `etag`. */
export const updateRequirementDraft = (id: string, etag: string, body: UpdateRequirementSetRequest) =>
  api.put<RequirementSetDto>(`/requirement-sets/${id}`, body, { ifMatch: etag })

export const publishRequirementSet = (id: string, etag: string) =>
  api.post<RequirementSetDto>(`/requirement-sets/${id}/publish`, undefined, { ifMatch: etag })

export const deleteRequirementDraft = (id: string, etag: string) => api.delete(`/requirement-sets/${id}`, { ifMatch: etag })

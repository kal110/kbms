import { api } from './client'
import type { GrantDto, GrantRequest, PrincipalDto, ResourceAccessDto } from './types'

// Folder and document permission grants (backend AccessEndpoints, frames 12–13).

export type AccessResource = 'folders' | 'documents'

export const getAccess = (resource: AccessResource, id: string, signal?: AbortSignal) =>
  api.get<ResourceAccessDto>(`/${resource}/${id}/access`, { signal })

/** Grants or changes a principal's level (needs Manage). */
export const grantAccess = (resource: AccessResource, id: string, body: GrantRequest) =>
  api.post<GrantDto>(`/${resource}/${id}/access`, body)

/** System grants from the org tree can't be revoked (409). */
export const revokeAccess = (resource: AccessResource, id: string, grantId: string) =>
  api.delete(`/${resource}/${id}/access/${grantId}`)

export const setInheritance = (resource: AccessResource, id: string, inherit: boolean) =>
  api.put<ResourceAccessDto>(`/${resource}/${id}/inheritance`, { inherit })

/** People, groups and org units (and their dean, supervisor or head) to grant access to. */
export const searchPrincipals = (q: string, signal?: AbortSignal) =>
  api.get<PrincipalDto[]>('/access/principals', { query: { q }, signal })

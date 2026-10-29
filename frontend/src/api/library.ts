import { api } from './client'
import type {
  ContentLink,
  CreateFolderRequest,
  DocumentDetail,
  DocumentState,
  FolderContents,
  FolderDetail,
  FolderGroups,
  FolderNode,
  OrgFolderSyncResult,
  RestoreVersionRequest,
  SearchResult,
  UpdateDocumentRequest,
  UpdateFolderRequest,
  VersionComparison,
  VersionDto,
} from './types'

// Folders, documents, versions and keyword search (backend LibraryEndpoints).

export type FolderTreeRoot = 'academic' | 'offices' | 'library' | 'all'

export interface DocumentListQuery {
  q?: string
  typeId?: string
  yearId?: string
  termId?: string
  state?: DocumentState
  includeSubfolders?: boolean
  /** See FolderService in the backend for the accepted values. */
  sort?: string
  page?: number
  pageSize?: number
}

export interface SearchQuery {
  q?: string
  typeId?: string
  yearId?: string
  termId?: string
  folderId?: string
  page?: number
  pageSize?: number
}

// ---- Folders

export const getFolderTree = (root?: FolderTreeRoot, signal?: AbortSignal) =>
  api.get<FolderNode[]>('/folders/tree', { query: { root }, signal })

/** The caller's own faculty/staff folders, where their submissions are filed. */
export const getMyFolders = (signal?: AbortSignal) => api.get<FolderNode[]>('/folders/me', { signal })

export const getFolder = (id: string, signal?: AbortSignal) => api.get<FolderDetail>(`/folders/${id}`, { signal })

export const getFolderChildren = (id: string, query: DocumentListQuery = {}, signal?: AbortSignal) =>
  api.get<FolderContents>(`/folders/${id}/children`, { query: { ...query }, signal })

/** Documents in the folder and below, counted by academic year and type (one term with termId). */
export const getFolderGroups = (id: string, termId?: string, signal?: AbortSignal) =>
  api.get<FolderGroups>(`/folders/${id}/groups`, { query: { termId }, signal })

export const createFolder = (body: CreateFolderRequest) => api.post<FolderDetail>('/folders', body)

export const updateFolder = (id: string, body: UpdateFolderRequest) => api.patch<FolderDetail>(`/folders/${id}`, body)

export const deleteFolder = (id: string) => api.delete(`/folders/${id}`)

/** Admin: rebuild the org folders and their system grants. */
export const syncOrgFolders = () => api.post<OrgFolderSyncResult>('/admin/org-sync/folders')

// ---- Documents

export const getDocument = (id: string, signal?: AbortSignal) => api.get<DocumentDetail>(`/documents/${id}`, { signal })

/** If-Match is required: pass the document's `eTag`. */
export const updateDocument = (id: string, eTag: string, body: UpdateDocumentRequest) =>
  api.patch<DocumentDetail>(`/documents/${id}`, body, { ifMatch: eTag })

export const getVersions = (id: string, signal?: AbortSignal) => api.get<VersionDto[]>(`/documents/${id}/versions`, { signal })

export const compareVersions = (id: string, from: number, to: number, signal?: AbortSignal) =>
  api.get<VersionComparison>(`/documents/${id}/versions/compare`, { query: { from, to }, signal })

export const restoreVersion = (id: string, versionId: string, body?: RestoreVersionRequest) =>
  api.post<DocumentDetail>(`/documents/${id}/versions/${versionId}/restore`, body ?? null)

export const archiveDocument = (id: string, eTag?: string) =>
  api.post<DocumentDetail>(`/documents/${id}/archive`, undefined, { ifMatch: eTag })

export const restoreDocument = (id: string, eTag?: string) =>
  api.post<DocumentDetail>(`/documents/${id}/restore`, undefined, { ifMatch: eTag })

export const deleteDocument = (id: string, eTag?: string) => api.delete(`/documents/${id}`, { ifMatch: eTag })

/**
 * A short-lived signed link to a version's bytes. `inline` previews (needs View), otherwise it
 * downloads (needs Download). Links can't carry the bearer token, so ask for the URL first.
 */
export const getContentLink = (id: string, options: { versionId?: string; inline?: boolean } = {}) =>
  api.get<ContentLink>(`/documents/${id}/content`, {
    query: { versionId: options.versionId, inline: options.inline ?? false, redirect: false },
  })

/** Opens a preview in a new tab, or starts the download. */
export async function openDocumentContent(id: string, options: { versionId?: string; inline?: boolean } = {}) {
  const link = await getContentLink(id, options)
  if (link.inline) window.open(link.url, '_blank', 'noopener')
  else window.location.assign(link.url)
  return link
}

// ---- Search

export const searchDocuments = (query: SearchQuery, signal?: AbortSignal) =>
  api.get<SearchResult>('/knowledge/search', { query: { ...query }, signal })

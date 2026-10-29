import { ApiError, api } from './client'
import type { CompleteUploadRequest, DocumentDetail, UploadTicket } from './types'

// Uploading is three steps (backend StorageEndpoints, frame 8):
//   1. POST /uploads checks type and size and returns a signed PUT URL (valid 30 minutes);
//   2. the browser PUTs the bytes straight to that URL (no bearer token: the URL is the permission);
//   3. either complete the upload into the library (completeUpload), or hand its id to a
//      submission checklist slot (addAttachment in ./workflow).

export interface UploadProgress {
  loaded: number
  total: number
}

export interface UploadOptions {
  /** Upload a new version of this document instead of a new document. */
  documentId?: string
  onProgress?: (progress: UploadProgress) => void
  signal?: AbortSignal
}

const contentTypeOf = (file: File) => file.type || 'application/octet-stream'

export const startUpload = (file: File, documentId?: string) =>
  api.post<UploadTicket>('/uploads', {
    fileName: file.name,
    contentType: contentTypeOf(file),
    size: file.size,
    sha256: null,
    documentId: documentId ?? null,
  })

/** PUTs the bytes to the ticket's signed URL. XHR rather than fetch, for upload progress. */
export function sendUploadBytes(ticket: UploadTicket, file: File, options: Pick<UploadOptions, 'onProgress' | 'signal'> = {}) {
  return new Promise<void>((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    xhr.open(ticket.method || 'PUT', ticket.url)
    for (const [name, value] of Object.entries(ticket.headers ?? {})) xhr.setRequestHeader(name, value)
    if (!Object.keys(ticket.headers ?? {}).some((h) => h.toLowerCase() === 'content-type')) {
      xhr.setRequestHeader('Content-Type', contentTypeOf(file))
    }
    xhr.upload.onprogress = (e) => options.onProgress?.({ loaded: e.loaded, total: e.lengthComputable ? e.total : file.size })
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) return resolve()
      let problem: Record<string, unknown> | undefined
      try {
        problem = JSON.parse(xhr.responseText) as Record<string, unknown>
      } catch {
        problem = undefined
      }
      const title = typeof problem?.title === 'string' ? problem.title : `Upload failed (${xhr.status})`
      reject(new ApiError(xhr.status, typeof problem?.code === 'string' ? problem.code : `http-${xhr.status}`, title, problem))
    }
    xhr.onerror = () => reject(new ApiError(0, 'network', "The file couldn't be sent. Check your connection and try again."))
    xhr.onabort = () => reject(new DOMException('Upload cancelled', 'AbortError'))
    options.signal?.addEventListener('abort', () => xhr.abort(), { once: true })
    xhr.send(file)
  })
}

/** Steps 1 and 2: returns the upload id, ready to complete or attach. */
export async function uploadFile(file: File, options: UploadOptions = {}): Promise<string> {
  const ticket = await startUpload(file, options.documentId)
  await sendUploadBytes(ticket, file, options)
  return ticket.uploadId
}

/** Step 3 for the library: files the bytes as a new document, or a new version of `documentId`. */
export const completeUpload = (uploadId: string, body: CompleteUploadRequest) =>
  api.post<DocumentDetail>(`/uploads/${uploadId}/complete`, body, { idempotencyKey: `complete-${uploadId}` })

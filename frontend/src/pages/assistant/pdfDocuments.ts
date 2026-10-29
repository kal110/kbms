import type { PDFDocumentLoadingTask, PDFDocumentProxy } from 'pdfjs-dist'
import { getContentLink } from '../../api/library'

// pdf.js draws cited pages in the citation preview. It is large, so it loads the first time a PDF
// is shown, and its worker is bundled by Vite (?worker) rather than served as a loose .mjs file.

export type PdfJs = typeof import('pdfjs-dist')

let pdfjs: Promise<PdfJs> | null = null

function loadPdfJs() {
  if (!pdfjs) {
    pdfjs = Promise.all([import('pdfjs-dist'), import('pdfjs-dist/build/pdf.worker.min.mjs?worker')]).then(([lib, worker]) => {
      lib.GlobalWorkerOptions.workerPort = new worker.default()
      return lib
    })
    pdfjs.catch(() => {
      pdfjs = null
    })
  }
  return pdfjs
}

export interface OpenedVersion {
  /** Null when the file isn't a PDF (Word, plain text): the preview shows its extracted text instead. */
  pdf: PDFDocumentProxy | null
  pdfjs: PdfJs | null
}

interface Opened {
  version: Promise<OpenedVersion>
  task?: PDFDocumentLoadingTask
}

// A few recently opened versions, newest last, so moving between citations doesn't download again.
// The signed link only has to last until pdf.js has fetched the file.
const opened = new Map<string, Opened>()
const KEEP = 3

/** A version's PDF, opened through a fresh preview link (which re-checks View and is audited). */
export function openVersion(documentId: string, versionId: string): Promise<OpenedVersion> {
  const known = opened.get(versionId)
  if (known) {
    opened.delete(versionId)
    opened.set(versionId, known)
    return known.version
  }
  const entry: Opened = {
    version: load(documentId, versionId, (task) => {
      entry.task = task
    }),
  }
  entry.version.catch(() => {
    if (opened.get(versionId) === entry) opened.delete(versionId)
  })
  opened.set(versionId, entry)
  for (const [id, old] of opened) {
    if (opened.size <= KEEP) break
    opened.delete(id)
    void old.task?.destroy()
  }
  return entry.version
}

async function load(documentId: string, versionId: string, started: (task: PDFDocumentLoadingTask) => void): Promise<OpenedVersion> {
  const link = await getContentLink(documentId, { versionId, inline: true })
  if (!/\.pdf$/i.test(link.fileName)) return { pdf: null, pdfjs: null }
  const lib = await loadPdfJs()
  const task = lib.getDocument({ url: link.url })
  started(task)
  return { pdf: await task.promise, pdfjs: lib }
}

/** Lets go of every opened version (leaving the assistant). */
export function closeVersions() {
  for (const old of opened.values()) void old.task?.destroy()
  opened.clear()
}

import { useEffect, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { ChevronLeft, ChevronRight, LocateFixed, Maximize2, Minimize2, ZoomIn, ZoomOut } from 'lucide-react'
import type { PageViewport, PDFDocumentProxy, RenderTask } from 'pdfjs-dist'
import type { TextItem, TextStyle } from 'pdfjs-dist/types/src/display/api'
import { getPageText } from '../../api/library'
import type { AssistantSource } from '../../api/types'
import { useApi } from '../../api/useApi'
import { ErrorState, Loading } from '../../components/AsyncState'
import { locatePassage } from './passageMarks'
import type { PieceMark } from './passageMarks'
import { openVersion } from './pdfDocuments'
import type { PdfJs } from './pdfDocuments'
import styles from './CitedDocument.module.css'

// The citation preview's page: the cited version's PDF page as it is, with the cited passage marked
// on it. Files the browser can't draw (Word, plain text) show the page's extracted text instead,
// marked the same way. The page can be turned and zoomed; the citation's page is one click away.

const ZOOMS = [0.75, 1, 1.25, 1.5, 2, 3]
const GAP = 16

/** How much of the passage was marked on a page (null while it is still being drawn). */
interface Found {
  page: number
  found: number
  /** No text on the page: a scan, whose text the server read by OCR, so there is nothing to mark. */
  scanned: boolean
}

export function CitedDocument({ source, wide, onToggleWide }: { source: AssistantSource; wide: boolean; onToggleWide: () => void }) {
  const cited = Math.max(1, source.pageStart)
  const citedEnd = Math.max(cited, source.pageEnd)
  // Turns saved before passages were kept only have the snippet.
  const passage = source.passage || source.snippet.replace(/…$/, '')
  const [page, setPage] = useState(cited)
  const [zoom, setZoom] = useState(1)
  const [found, setFound] = useState<Found | null>(null)

  const opened = useApi(() => openVersion(source.documentId, source.versionId), [source.documentId, source.versionId])
  const textMode = opened.data !== undefined && !opened.data.pdf
  const text = useApi(
    (signal) => (textMode ? getPageText(source.documentId, source.versionId, page, signal) : Promise.resolve(null)),
    [textMode, source.documentId, source.versionId, page],
  )
  const textMarks = useMemo(() => (text.data ? locatePassage([text.data.text], passage) : null), [text.data, passage])

  const pages = opened.data?.pdf?.numPages ?? text.data?.pages ?? null
  const marked = textMode ? (textMarks && text.data?.page === page ? textMarks.found : null) : found?.page === page ? found.found : null
  const away = page < cited || page > citedEnd
  const zoomAt = ZOOMS.indexOf(zoom)

  let body: ReactNode
  if (opened.error) body = <ErrorState error={opened.error} onRetry={opened.reload} compact />
  else if (!opened.data) body = <Loading label="Opening the document…" compact />
  else if (opened.data.pdf && opened.data.pdfjs)
    body = <PdfPage pdf={opened.data.pdf} pdfjs={opened.data.pdfjs} page={page} zoom={zoom} passage={passage} onFound={setFound} />
  else if (text.error) body = <ErrorState error={text.error} onRetry={text.reload} compact />
  else if (!text.data || !textMarks || text.data.page !== page) body = <Loading label="Loading the page…" compact />
  else body = <TextPage text={text.data.text} marks={textMarks.marks} />

  return (
    <div className={styles.document}>
      <div className={styles.toolbar}>
        <span className={styles.group}>
          <IconButton label="Previous page" disabled={page <= 1} onClick={() => setPage(page - 1)}>
            <ChevronLeft size={15} />
          </IconButton>
          <span className={styles.pageLabel} aria-live="polite">
            Page {page}
            {pages ? ` of ${pages}` : ''}
          </span>
          <IconButton label="Next page" disabled={pages !== null && page >= pages} onClick={() => setPage(page + 1)}>
            <ChevronRight size={15} />
          </IconButton>
        </span>
        {away && (
          <button type="button" className={styles.back} onClick={() => setPage(cited)}>
            <LocateFixed size={13} aria-hidden />
            Cited page
          </button>
        )}
        <span className={styles.group}>
          {!textMode && (
            <>
              <IconButton label="Zoom out" disabled={zoomAt <= 0} onClick={() => setZoom(ZOOMS[zoomAt - 1])}>
                <ZoomOut size={15} />
              </IconButton>
              <span className={styles.zoomLabel}>{Math.round(zoom * 100)}%</span>
              <IconButton label="Zoom in" disabled={zoomAt >= ZOOMS.length - 1} onClick={() => setZoom(ZOOMS[zoomAt + 1])}>
                <ZoomIn size={15} />
              </IconButton>
            </>
          )}
          <IconButton label={wide ? 'Narrow the preview' : 'Widen the preview'} pressed={wide} onClick={onToggleWide}>
            {wide ? <Minimize2 size={14} /> : <Maximize2 size={14} />}
          </IconButton>
        </span>
      </div>
      <MarkNote page={page} cited={cited} citedEnd={citedEnd} away={away} marked={marked} scanned={!textMode && found?.page === page && found.scanned} />
      {body}
      {!away && marked === 0 && (
        <blockquote className={styles.quote}>
          <span>Cited passage</span>“{source.snippet}”
        </blockquote>
      )}
    </div>
  )
}

function MarkNote({
  page,
  cited,
  citedEnd,
  away,
  marked,
  scanned,
}: {
  page: number
  cited: number
  citedEnd: number
  away: boolean
  marked: number | null
  scanned: boolean
}) {
  let note: string | null = null
  let tone = styles.noteMarked
  if (away) {
    note = cited === citedEnd ? `The citation is on page ${cited}.` : `The citation is on pages ${cited}–${citedEnd}.`
    tone = styles.noteAway
  } else if (marked === 0 && scanned) {
    note = 'This page is a scanned image, so the passage can’t be marked on it. It is quoted below, as read from the scan.'
    tone = styles.noteAway
  } else if (marked === 0) {
    note = 'The passage couldn’t be marked on this page, so it is quoted below.'
    tone = styles.noteAway
  } else if (marked !== null) {
    note =
      page < citedEnd
        ? `Cited passage marked. It continues on page ${page + 1}.`
        : page > cited
          ? `Cited passage marked. It starts on page ${cited}.`
          : 'Cited passage marked.'
  }
  if (!note) return <div className={styles.note} aria-hidden />
  return (
    <div className={`${styles.note} ${tone}`} role="status">
      {!away && marked !== 0 && <span className={styles.swatch} aria-hidden />}
      {note}
    </div>
  )
}

function IconButton({
  label,
  pressed,
  disabled,
  onClick,
  children,
}: {
  label: string
  pressed?: boolean
  disabled?: boolean
  onClick: () => void
  children: ReactNode
}) {
  return (
    <button type="button" className={styles.iconButton} aria-label={label} title={label} aria-pressed={pressed} disabled={disabled} onClick={onClick}>
      {children}
    </button>
  )
}

interface Box {
  left: number
  top: number
  width: number
  height: number
}

interface Drawn {
  key: string
  width: number
  height: number
  boxes: Box[]
}

/** One PDF page fitted to the panel's width (times the zoom), the passage boxed over it. */
function PdfPage({
  pdf,
  pdfjs,
  page,
  zoom,
  passage,
  onFound,
}: {
  pdf: PDFDocumentProxy
  pdfjs: PdfJs
  page: number
  zoom: number
  passage: string
  onFound: (found: Found) => void
}) {
  const scroller = useRef<HTMLDivElement>(null)
  const canvas = useRef<HTMLCanvasElement>(null)
  const [width, setWidth] = useState(0)
  const [drawn, setDrawn] = useState<Drawn | null>(null)
  const [failed, setFailed] = useState<{ key: string; error: unknown } | null>(null)
  const key = `${page}:${zoom}:${width}:${passage}`

  useEffect(() => {
    const el = scroller.current
    if (!el) return
    const observer = new ResizeObserver(() => setWidth(el.clientWidth))
    observer.observe(el)
    return () => {
      observer.disconnect()
    }
  }, [])

  useEffect(() => {
    const target = canvas.current
    if (!width || !target) return
    let cancelled = false
    let task: RenderTask | undefined
    const draw = async () => {
      const pdfPage = await pdf.getPage(page)
      if (cancelled) return
      const fit = Math.max(120, width - 2 * GAP) / pdfPage.getViewport({ scale: 1 }).width
      const viewport = pdfPage.getViewport({ scale: fit * zoom })
      const ratio = window.devicePixelRatio || 1
      target.width = Math.floor(viewport.width * ratio)
      target.height = Math.floor(viewport.height * ratio)
      target.style.width = `${viewport.width}px`
      target.style.height = `${viewport.height}px`
      task = pdfPage.render({ canvas: target, viewport, transform: ratio === 1 ? undefined : [ratio, 0, 0, ratio, 0, 0] })
      const [content] = await Promise.all([pdfPage.getTextContent(), task.promise])
      if (cancelled) return
      const items = content.items.filter((item): item is TextItem => 'str' in item)
      const located = locatePassage(
        items.map((item) => item.str),
        passage,
      )
      const boxes = located.marks.flatMap((mark) => boxesOf(items[mark.piece], mark, viewport, content.styles[items[mark.piece].fontName], pdfjs))
      setDrawn({ key, width: viewport.width, height: viewport.height, boxes })
      onFound({ page, found: boxes.length > 0 ? located.found : 0, scanned: items.every((item) => !item.str.trim()) })
    }
    draw().catch((error: unknown) => {
      if (!cancelled && !(error instanceof pdfjs.RenderingCancelledException)) setFailed({ key, error })
    })
    return () => {
      cancelled = true
      task?.cancel()
    }
  }, [pdf, pdfjs, page, zoom, width, passage, key, onFound])

  const shown = drawn?.key === key ? drawn : null

  // Bring the first mark into view, a third of the way down (the page scrolls inside the panel only).
  useEffect(() => {
    const el = scroller.current
    if (!el || !shown) return
    const first = shown.boxes[0]
    el.scrollTo({
      top: first ? Math.max(0, first.top + GAP - el.clientHeight / 3) : 0,
      left: first ? Math.max(0, first.left + GAP - el.clientWidth / 4) : 0,
      behavior: 'smooth',
    })
  }, [shown])

  const error = failed?.key === key ? failed.error : null
  return (
    <div ref={scroller} className={styles.canvas}>
      <div className={styles.sheet} style={shown ? { width: shown.width, height: shown.height } : { visibility: 'hidden' }}>
        <canvas ref={canvas} role="img" aria-label={`Page ${page} of the cited document`} />
        {shown?.boxes.map((box, i) => (
          <span key={i} className={styles.mark} style={{ left: box.left, top: box.top, width: box.width, height: box.height }} />
        ))}
      </div>
      {!shown && !error && (
        <div className={styles.overlay}>
          <Loading label="Drawing the page…" compact />
        </div>
      )}
      {error !== null && (
        <div className={styles.overlay}>
          <ErrorState error={error} compact />
        </div>
      )}
    </div>
  )
}

/** Where marked characters of a text item sit on the drawn page (horizontal text only). */
function boxesOf(item: TextItem, mark: PieceMark, viewport: PageViewport, style: TextStyle | undefined, pdfjs: PdfJs): Box[] {
  const tx: number[] = pdfjs.Util.transform(viewport.transform, item.transform)
  if (tx[0] <= 0 || Math.abs(tx[1]) > Math.abs(tx[0]) * 0.01) return []
  const size = Math.hypot(tx[2], tx[3])
  // As pdf.js's own text layer places text: the font's ascent, or 0.8 of its size when unknown.
  const ascent = style?.ascent ? style.ascent : style?.descent ? 1 + style.descent : 0.8
  const width = item.width * viewport.scale
  const chars = item.str.length || 1
  return [
    {
      left: tx[4] + (width * mark.from) / chars - 1,
      top: tx[5] - size * ascent - 1,
      width: (width * (mark.to - mark.from)) / chars + 2,
      height: size + 2,
    },
  ]
}

/** A page of extracted text, the passage marked. */
function TextPage({ text, marks }: { text: string; marks: PieceMark[] }) {
  const scroller = useRef<HTMLDivElement>(null)
  const first = useRef<HTMLElement>(null)

  useEffect(() => {
    const el = scroller.current
    const mark = first.current
    if (!el) return
    el.scrollTo({ top: mark ? Math.max(0, mark.offsetTop - el.clientHeight / 3) : 0, behavior: 'smooth' })
  }, [text, marks])

  const mark = marks[0]
  return (
    <div ref={scroller} className={styles.canvas}>
      <div className={styles.textSheet}>
        {mark ? (
          <>
            {text.slice(0, mark.from)}
            <mark ref={first} className={styles.textMark}>
              {text.slice(mark.from, mark.to)}
            </mark>
            {text.slice(mark.to)}
          </>
        ) : (
          text || 'This page has no text.'
        )}
      </div>
    </div>
  )
}

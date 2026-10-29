import { Fragment, useEffect, useRef, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { BadgeCheck, ExternalLink, Lock, Search, Sparkles, TriangleAlert } from 'lucide-react'
import { Link } from 'react-router'
import { errorMessage, isAbort } from '../../api/client'
import { askAssistant, getAssistantStatus } from '../../api/knowledge'
import { getDocumentTypes } from '../../api/reference'
import type { AssistantAnswer, AssistantSource } from '../../api/types'
import { useApi } from '../../api/useApi'
import { ErrorState, Loading } from '../../components/AsyncState'
import { Button } from '../../components/Button'
import { PageHeader } from '../../components/PageHeader'
import styles from './AssistantPage.module.css'

// Frame 5. Answers come only from passages the API re-checked against the caller's access, and
// every claim cites one ([n]). The text streams in; the final "done" event replaces it with the
// server's citation-mapped answer, or with "not enough evidence" when nothing grounded it.

type Scope = 'published' | 'my-drafts'

interface Turn {
  id: number
  question: string
  scope: Scope
  sources: AssistantSource[]
  text: string
  answer?: AssistantAnswer
  error?: string
  pending: boolean
}

interface Selection {
  turn: number
  source: AssistantSource
}

export function AssistantPage() {
  const status = useApi((signal) => getAssistantStatus(signal), [])
  const types = useApi(() => getDocumentTypes(), [])
  const [scope, setScope] = useState<Scope>('published')
  const [typeId, setTypeId] = useState('')
  const [question, setQuestion] = useState('')
  const [turns, setTurns] = useState<Turn[]>([])
  const [selected, setSelected] = useState<Selection | null>(null)
  const abort = useRef<AbortController | null>(null)
  const bottom = useRef<HTMLDivElement>(null)

  const enabled = status.data?.enabled === true
  const busy = turns.some((t) => t.pending)
  const maxChars = status.data?.maxQuestionChars ?? 2000

  useEffect(() => () => abort.current?.abort(), [])
  // A block body: scrollIntoView returns a Promise in current browsers, and an effect must not return one.
  useEffect(() => {
    bottom.current?.scrollIntoView({ block: 'end' })
  }, [turns])

  const update = (id: number, change: (turn: Turn) => Partial<Turn>) =>
    setTurns((all) => all.map((t) => (t.id === id ? { ...t, ...change(t) } : t)))

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    const text = question.trim()
    if (!enabled || !text || busy) return
    setQuestion('')
    const id = (turns.at(-1)?.id ?? 0) + 1
    setTurns((all) => [...all, { id, question: text, scope, sources: [], text: '', pending: true }])

    const controller = new AbortController()
    abort.current = controller
    try {
      const stream = askAssistant({ question: text, scope, documentTypeId: typeId || null }, controller.signal)
      for await (const event of stream) {
        if (event.type === 'sources') update(id, () => ({ sources: event.sources }))
        else if (event.type === 'delta') update(id, (t) => ({ text: t.text + event.text }))
        else if (event.type === 'done') update(id, () => ({ answer: event.answer, text: event.answer.text }))
        else update(id, () => ({ error: event.message }))
      }
    } catch (error) {
      if (!isAbort(error)) update(id, () => ({ error: errorMessage(error) }))
    } finally {
      update(id, () => ({ pending: false }))
    }
  }

  return (
    <>
      <PageHeader
        title="AI Assistant"
        description="Answers come only from documents you can open. Every claim is cited."
        actions={
          <div className={styles.scopeToggle} role="radiogroup" aria-label="Search scope">
            <ScopeOption on={scope === 'published'} disabled={!enabled || busy} onClick={() => setScope('published')}>
              <BadgeCheck size={15} className={styles.iconPublished} aria-hidden />
              Official published
            </ScopeOption>
            <ScopeOption on={scope === 'my-drafts'} disabled={!enabled || busy} onClick={() => setScope('my-drafts')}>
              <Lock size={15} className={styles.iconDraft} aria-hidden />
              My drafts (private)
            </ScopeOption>
          </div>
        }
      />

      {status.error && !status.data ? (
        <ErrorState error={status.error} onRetry={status.reload} />
      ) : !status.data ? (
        <Loading label="Checking the assistant…" />
      ) : (
        <>
          {enabled && (
            <div className={styles.filters}>
              <label className={styles.filter}>
                <span className={styles.filterLabel}>Type</span>
                <select value={typeId} onChange={(e) => setTypeId(e.target.value)} disabled={busy} aria-label="Document type">
                  <option value="">Any</option>
                  {(types.data ?? []).map((t) => (
                    <option key={t.id} value={t.id}>
                      {t.name}
                    </option>
                  ))}
                </select>
              </label>
              <span className={styles.filterNote}>
                {scope === 'published'
                  ? 'Searching the published version of each document you can open.'
                  : 'Searching only your own unpublished current versions. Nobody else sees these results.'}
              </span>
            </div>
          )}
          <div className={styles.body}>
            <section className={styles.chat} aria-label="Conversation">
              <div className={styles.messages} aria-live="polite">
                {!enabled && <OffState reason={status.data.reason} onRetry={status.reload} />}
                {enabled && turns.length === 0 && (
                  <div className={styles.offState}>
                    <span className={styles.offIcon}>
                      <Sparkles size={22} aria-hidden />
                    </span>
                    <span className={styles.offTitle}>Ask about policies, syllabi, memos…</span>
                    <span className={styles.offText}>
                      Answers cite the version and page they come from. If nothing you can open answers the question,
                      the assistant says so instead of guessing.
                    </span>
                  </div>
                )}
                {turns.map((t) => (
                  <div key={t.id} className={styles.attempt}>
                    <div className={styles.userRow}>
                      <div className={styles.userBubble}>{t.question}</div>
                    </div>
                    <TurnView
                      turn={t}
                      selected={selected?.turn === t.id ? selected.source.number : null}
                      onSelect={(source) => setSelected({ turn: t.id, source })}
                    />
                  </div>
                ))}
                <div ref={bottom} />
              </div>

              <div className={styles.divider} />
              <form className={styles.composer} onSubmit={(e) => void submit(e)}>
                <input
                  className={styles.input}
                  value={question}
                  maxLength={maxChars}
                  disabled={!enabled}
                  onChange={(e) => setQuestion(e.target.value)}
                  placeholder={enabled ? 'Ask about policies, syllabi, memos…' : 'The assistant isn’t available yet'}
                  aria-label="Question"
                />
                <Button type="submit" variant="primary" disabled={!enabled || !question.trim() || busy}>
                  Send
                </Button>
              </form>
            </section>

            <CitationPreview selection={selected?.source ?? null} enabled={enabled} />
          </div>
        </>
      )}
    </>
  )
}

function ScopeOption({ on, disabled, onClick, children }: { on: boolean; disabled: boolean; onClick: () => void; children: ReactNode }) {
  return (
    <button
      type="button"
      role="radio"
      aria-checked={on}
      className={`${styles.scopeOption} ${on ? styles.scopeOn : ''}`}
      disabled={disabled}
      onClick={onClick}
    >
      {children}
    </button>
  )
}

function OffState({ reason, onRetry }: { reason: string | null; onRetry: () => void }) {
  return (
    <div className={styles.offState}>
      <span className={styles.offIcon}>
        <Sparkles size={22} aria-hidden />
      </span>
      <span className={styles.offTitle}>The AI assistant isn’t available right now</span>
      <span className={styles.offText}>{reason ?? 'It hasn’t been switched on for this system.'}</span>
      <span className={styles.offText}>
        When it is, it answers from the documents you can open, cites the page behind every claim, and says so when
        nothing you can open answers the question.
      </span>
      <span className={styles.offActions}>
        <Button variant="secondary" onClick={onRetry}>
          Check again
        </Button>
        <Link to="/search" className={styles.offLink}>
          <Search size={14} aria-hidden />
          Search documents by keyword instead
        </Link>
      </span>
    </div>
  )
}

function TurnView({ turn, selected, onSelect }: { turn: Turn; selected: number | null; onSelect: (s: AssistantSource) => void }) {
  const { answer } = turn
  const draft = turn.scope === 'my-drafts'
  const sourceFor = (n: number) => (answer?.citations ?? turn.sources).find((s) => s.number === n)

  if (turn.error && !answer) {
    return (
      <div className={styles.insufficient} role="alert">
        <span className={styles.insufficientHead}>
          <TriangleAlert size={16} aria-hidden />
          No answer
        </span>
        <span>{turn.error}</span>
        {turn.sources.length > 0 && <SourceList title="Passages found" sources={turn.sources.slice(0, 3)} draft={draft} selected={selected} onSelect={onSelect} />}
      </div>
    )
  }

  if (answer?.outcome === 'InsufficientEvidence' || answer?.outcome === 'Declined') {
    return (
      <div className={styles.insufficient}>
        <span className={styles.insufficientHead}>
          <TriangleAlert size={16} aria-hidden />
          {answer.outcome === 'Declined' ? 'Can’t answer' : 'Not enough evidence'}
        </span>
        <span>{answer.text}</span>
        {answer.citations.length > 0 && <SourceList title="Closest passages" sources={answer.citations} draft={draft} selected={selected} onSelect={onSelect} />}
      </div>
    )
  }

  if (!turn.text && turn.pending) {
    return (
      <span className={styles.thinking}>
        <Sparkles size={14} aria-hidden />
        {turn.sources.length ? `Reading ${turn.sources.length} passages…` : 'Searching documents you can open…'}
      </span>
    )
  }

  const cited = answer?.citations.length ?? 0
  return (
    <div className={styles.answer}>
      <span className={styles.answerHead}>
        <Sparkles size={16} aria-hidden />
        {answer
          ? `Answer · based on ${cited} ${draft ? 'private draft' : 'published'} source${cited === 1 ? '' : 's'}`
          : 'Writing the answer…'}
      </span>
      <div className={styles.answerText}>
        <AnswerText
          text={turn.text}
          cite={(n) => {
            const source = sourceFor(n)
            // Until "done" maps citations, an unknown number is hidden rather than shown as a dead chip.
            return source ? (
              <button type="button" className={`${styles.cite} ${selected === n ? styles.citeOn : ''}`} onClick={() => onSelect(source)}>
                {n}
              </button>
            ) : null
          }}
        />
      </div>
      {answer && cited > 0 && <SourceList sources={answer.citations} draft={draft} selected={selected} onSelect={onSelect} />}
      {turn.error && <span className={styles.answerError}>{turn.error}</span>}
    </div>
  )
}

/** Paragraphs, **bold** and [n] citation chips; everything else is plain text. */
function AnswerText({ text, cite }: { text: string; cite: (n: number) => ReactNode }) {
  return (
    <>
      {text.split(/\n{2,}/).map((paragraph, p) => (
        <p key={p}>
          {paragraph.split(/(\[\d+\]|\*\*[^*]+\*\*)/).map((part, i) => {
            const marker = /^\[(\d+)\]$/.exec(part)
            if (marker) return <Fragment key={i}>{cite(Number(marker[1]))}</Fragment>
            if (part.startsWith('**') && part.endsWith('**') && part.length > 4) return <strong key={i}>{part.slice(2, -2)}</strong>
            return <Fragment key={i}>{part}</Fragment>
          })}
        </p>
      ))}
    </>
  )
}

function SourceList({ title, sources, draft, selected, onSelect }: {
  title?: string
  sources: AssistantSource[]
  draft: boolean
  selected: number | null
  onSelect: (s: AssistantSource) => void
}) {
  return (
    <div className={styles.sources}>
      {title && <span className={styles.sourcesTitle}>{title}</span>}
      {sources.map((s) => (
        <div key={s.number} className={`${styles.source} ${selected === s.number ? styles.sourceOn : ''}`}>
          <button type="button" className={styles.sourceMain} onClick={() => onSelect(s)}>
            <span className={styles.sourceNumber}>{s.number}</span>
            <span className={styles.sourceBody}>
              <span className={styles.sourceTitle}>{[s.title, s.documentNumber].filter(Boolean).join(' · ')}</span>
              <SourceMeta source={s} draft={draft} />
              <span className={styles.sourceSnippet}>“{s.snippet}”</span>
            </span>
          </button>
          <Link to={documentLink(s)} className={styles.sourceOpen}>
            Open p. {s.pageStart || 1} →
          </Link>
        </div>
      ))}
    </div>
  )
}

function SourceMeta({ source, draft }: { source: AssistantSource; draft: boolean }) {
  // The my-drafts scope only ever returns the caller's own unpublished versions.
  const parts = [`Version ${source.versionNumber} (${draft ? 'private draft' : 'published'})`, pages(source), source.section].filter(Boolean)
  return <span className={draft ? styles.sourceMetaDraft : styles.sourceMeta}>{parts.join(' · ')}</span>
}

function CitationPreview({ selection, enabled }: { selection: AssistantSource | null; enabled: boolean }) {
  if (!selection) {
    return (
      <aside className={styles.preview} aria-label="Citation preview">
        <div className={styles.previewEmpty}>
          {enabled
            ? 'Select a citation to preview the passage it comes from.'
            : 'Citations appear here once the assistant is available. Each one opens the cited page, and access is re-checked when you open it.'}
        </div>
      </aside>
    )
  }
  const s = selection
  return (
    <aside className={styles.preview} aria-label="Citation preview">
      <div className={styles.previewHead}>
        <span className={styles.previewTitle}>Citation [{s.number}]</span>
        <span className={styles.previewSub}>{[s.documentNumber ?? s.title, `v${s.versionNumber}`, pages(s)].filter(Boolean).join(' · ')}</span>
      </div>
      <div className={styles.previewCanvas}>
        <div className={styles.previewPage}>
          <span className={styles.previewDoc}>{s.title}</span>
          {s.section && <span className={styles.previewSection}>{s.section}</span>}
          <span className={styles.previewHighlight}>{s.snippet}</span>
          {s.documentType && <span className={styles.previewType}>{s.documentType}</span>}
        </div>
      </div>
      <div className={styles.previewFoot}>
        <Link to={documentLink(s)} className={styles.previewOpen}>
          <ExternalLink size={14} aria-hidden />
          Open version {s.versionNumber}, {pages(s)}
        </Link>
        <span>Access is re-checked when the document opens.</span>
      </div>
    </aside>
  )
}

const pages = (s: AssistantSource) =>
  s.pageStart <= 0 ? '' : s.pageEnd > s.pageStart ? `pages ${s.pageStart}–${s.pageEnd}` : `page ${s.pageStart}`

const documentLink = (s: AssistantSource) => `/documents/${s.documentId}?v=${s.versionNumber}#page=${Math.max(1, s.pageStart)}`

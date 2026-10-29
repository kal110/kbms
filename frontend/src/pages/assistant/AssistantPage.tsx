import { Fragment, useEffect, useRef, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import { BadgeCheck, ExternalLink, Lock, PanelLeftClose, PanelLeftOpen, Search, Sparkles, SquarePen, Trash2, TriangleAlert } from 'lucide-react'
import { Link, useSearchParams } from 'react-router'
import { errorMessage, isAbort } from '../../api/client'
import { askAssistant, deleteConversation, getAssistantStatus, getConversation, listConversations } from '../../api/knowledge'
import { getDocumentTypes } from '../../api/reference'
import type { AssistantAnswer, AssistantSource, ConversationSummary, ConversationTurn } from '../../api/types'
import { useAction, useApi } from '../../api/useApi'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Button } from '../../components/Button'
import { PageHeader } from '../../components/PageHeader'
import { timeAgo } from '../../lib/format'
import { CitedDocument } from './CitedDocument'
import { closeVersions } from './pdfDocuments'
import styles from './AssistantPage.module.css'

// Frame 5. Answers come only from passages the API re-checked against the caller's access, and
// every claim cites one ([n]). The text streams in; the final "done" event replaces it with the
// server's citation-mapped answer, or with "not enough evidence" when nothing grounded it.
//
// Conversations are kept per account on the server (only their owner sees them): ?c=<id> opens
// one, plain /assistant reopens the most recent, and ?new starts another. Follow-up questions go
// to the same conversation, so the assistant knows what "it" refers to.

type Scope = 'published' | 'my-drafts'

const HISTORY_KEY = 'kbms.assistant.history'
const PREVIEW_KEY = 'kbms.assistant.preview'
const INTERRUPTED = 'This answer was interrupted before it finished. Ask again to get it.'

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

/** A saved turn as the page shows it; an answer that never finished reads as interrupted. */
const fromSaved = (t: ConversationTurn): Turn => ({
  id: t.number,
  question: t.question,
  scope: t.scope === 'my-drafts' ? 'my-drafts' : 'published',
  sources: t.sources,
  text: t.answer?.text ?? '',
  answer: t.answer ?? undefined,
  error: t.error ?? (t.answer ? undefined : INTERRUPTED),
  pending: false,
})

function readHistoryOpen() {
  try {
    return window.localStorage.getItem(HISTORY_KEY) !== 'closed'
  } catch {
    return true
  }
}

/** The citation preview opens wide (the page readable) unless it was narrowed. */
function readPreviewWide() {
  try {
    return window.localStorage.getItem(PREVIEW_KEY) !== 'narrow'
  } catch {
    return true
  }
}

export function AssistantPage() {
  const status = useApi((signal) => getAssistantStatus(signal), [])
  const types = useApi(() => getDocumentTypes(), [])
  const history = useApi((signal) => listConversations(signal), [])
  const [params, setParams] = useSearchParams()
  const conversationParam = params.get('c')
  const startNew = params.has('new')
  const [scope, setScope] = useState<Scope>('published')
  const [typeId, setTypeId] = useState('')
  const [question, setQuestion] = useState('')
  const [turns, setTurns] = useState<Turn[]>([])
  /** The conversation the turns on screen belong to; null for a new chat not yet saved. */
  const [shownId, setShownId] = useState<string | null>(null)
  const [openFailed, setOpenFailed] = useState<{ id: string; error: unknown } | null>(null)
  const [historyOpen, setHistoryOpen] = useState(readHistoryOpen)
  const [previewWide, setPreviewWide] = useState(readPreviewWide)
  const [selected, setSelected] = useState<Selection | null>(null)
  const abort = useRef<AbortController | null>(null)
  const bottom = useRef<HTMLDivElement>(null)

  const enabled = status.data?.enabled === true
  const busy = turns.some((t) => t.pending)
  const maxChars = status.data?.maxQuestionChars ?? 2000
  const conversations = history.data?.items ?? []
  // ?c= names a conversation that isn't on screen yet: it is opening, or failed to.
  const opening =
    conversationParam && conversationParam !== shownId
      ? { id: conversationParam, error: openFailed?.id === conversationParam ? openFailed.error : undefined }
      : null

  useEffect(() => () => abort.current?.abort(), [])
  // Cited PDFs stay open while moving between citations, and are let go of with the page.
  useEffect(() => closeVersions, [])
  // A block body: scrollIntoView returns a Promise in current browsers, and an effect must not return one.
  useEffect(() => {
    bottom.current?.scrollIntoView({ block: 'end' })
  }, [turns])

  // Plain /assistant reopens the most recent conversation, so a chat survives leaving the page.
  const latestId = history.data?.items[0]?.id
  const blank = turns.length === 0
  useEffect(() => {
    if (!conversationParam && !startNew && blank && latestId) setParams({ c: latestId }, { replace: true })
  }, [conversationParam, startNew, blank, latestId, setParams])

  // ?c= names a conversation that isn't on screen: load it. One the page just saved is already shown.
  useEffect(() => {
    if (!conversationParam || conversationParam === shownId) return
    const controller = new AbortController()
    getConversation(conversationParam, controller.signal).then(
      (detail) => {
        setTurns(detail.turns.map(fromSaved))
        setShownId(detail.id)
        setSelected(null)
        setOpenFailed(null)
      },
      (error: unknown) => {
        if (!isAbort(error)) setOpenFailed({ id: conversationParam, error })
      },
    )
    return () => controller.abort()
  }, [conversationParam, shownId])

  // Back to a new chat (the browser's back button, a deleted conversation): clear the screen when ?c=
  // goes away, while rendering, as React does for state that follows a changed input. Only on the
  // change: a new chat names itself in the URL a moment after it is shown (the router's transition).
  const [seenParam, setSeenParam] = useState(conversationParam)
  if (seenParam !== conversationParam) {
    setSeenParam(conversationParam)
    if (!conversationParam && shownId) {
      setTurns([])
      setShownId(null)
      setSelected(null)
    }
  }

  const update = (id: number, change: (turn: Turn) => Partial<Turn>) =>
    setTurns((all) => all.map((t) => (t.id === id ? { ...t, ...change(t) } : t)))

  const toggleHistory = () => {
    const open = !historyOpen
    setHistoryOpen(open)
    try {
      window.localStorage.setItem(HISTORY_KEY, open ? 'open' : 'closed')
    } catch {
      // Private windows may refuse storage; the choice then lasts for this visit.
    }
  }

  const togglePreviewWide = () => {
    const wide = !previewWide
    setPreviewWide(wide)
    try {
      window.localStorage.setItem(PREVIEW_KEY, wide ? 'wide' : 'narrow')
    } catch {
      // As above: the choice lasts for this visit.
    }
  }

  const newChat = () => {
    setTurns([])
    setShownId(null)
    setSelected(null)
    setOpenFailed(null)
    setParams({ new: '' })
  }

  const remove = useAction(async (id: string) => {
    await deleteConversation(id)
    history.reload()
    if (id === shownId || id === conversationParam) newChat()
    return true
  })

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    const text = question.trim()
    if (!enabled || !text || busy || opening) return
    setQuestion('')
    const id = (turns.at(-1)?.id ?? 0) + 1
    const conversationId = shownId
    setTurns((all) => [...all, { id, question: text, scope, sources: [], text: '', pending: true }])

    const controller = new AbortController()
    abort.current = controller
    try {
      const stream = askAssistant({ question: text, scope, documentTypeId: typeId || null, conversationId }, controller.signal)
      for await (const event of stream) {
        if (event.type === 'sources') {
          update(id, () => ({ sources: event.sources }))
          if (event.conversationId !== conversationId) {
            // A new chat was just saved: name it in the URL without reloading what's on screen.
            setShownId(event.conversationId)
            setParams({ c: event.conversationId }, { replace: true })
          }
        } else if (event.type === 'delta') update(id, (t) => ({ text: t.text + event.text }))
        else if (event.type === 'done') update(id, () => ({ answer: event.answer, text: event.answer.text }))
        else update(id, () => ({ error: event.message }))
      }
    } catch (error) {
      if (!isAbort(error)) update(id, () => ({ error: errorMessage(error) }))
    } finally {
      update(id, () => ({ pending: false }))
      history.reload()
    }
  }

  return (
    <>
      <PageHeader
        title="AI Assistant"
        description="Answers come only from documents you can open. Every claim is cited."
        actions={
          <>
            <Button
              variant="ghost"
              icon={historyOpen ? PanelLeftClose : PanelLeftOpen}
              aria-pressed={historyOpen}
              onClick={toggleHistory}
            >
              {historyOpen ? 'Hide history' : 'History'}
            </Button>
            <Button icon={SquarePen} onClick={newChat} disabled={busy || (!shownId && !opening && turns.length === 0)}>
              New chat
            </Button>
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
          </>
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
            {historyOpen && (
              <ConversationList
                conversations={conversations}
                loading={!history.data && !history.error}
                error={history.data ? null : history.error}
                onRetry={history.reload}
                current={shownId ?? opening?.id ?? null}
                disabled={busy}
                onOpen={(id) => {
                  setOpenFailed(null)
                  setParams({ c: id })
                }}
                onDelete={(id) => remove.run(id)}
                deleting={remove.pending}
                deleteError={remove.error}
              />
            )}
            <section className={styles.chat} aria-label="Conversation">
              <div className={styles.messages} aria-live="polite">
                {opening?.error ? (
                  <div className={styles.offState}>
                    <span className={styles.offTitle}>This conversation couldn’t be opened</span>
                    <span className={styles.offText}>{errorMessage(opening.error)}</span>
                    <span className={styles.offActions}>
                      <Button variant="secondary" icon={SquarePen} onClick={newChat}>
                        Start a new chat
                      </Button>
                    </span>
                  </div>
                ) : opening ? (
                  <Loading label="Opening the conversation…" compact />
                ) : null}
                {!enabled && !opening && turns.length === 0 && <OffState reason={status.data.reason} onRetry={status.reload} />}
                {!enabled && turns.length > 0 && (
                  <span className={styles.scopeNote}>The assistant isn’t available right now. You can read this conversation but not continue it.</span>
                )}
                {enabled && !opening && turns.length === 0 && (
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
                  disabled={!enabled || Boolean(opening)}
                  onChange={(e) => setQuestion(e.target.value)}
                  placeholder={
                    !enabled ? 'The assistant isn’t available yet' : turns.length > 0 ? 'Ask a follow-up question…' : 'Ask about policies, syllabi, memos…'
                  }
                  aria-label="Question"
                />
                <Button type="submit" variant="primary" disabled={!enabled || !question.trim() || busy || Boolean(opening)}>
                  Send
                </Button>
              </form>
            </section>

            <CitationPreview selection={selected} enabled={enabled} wide={previewWide} onToggleWide={togglePreviewWide} />
          </div>
        </>
      )}
    </>
  )
}

function ConversationList({
  conversations,
  loading,
  error,
  onRetry,
  current,
  disabled,
  onOpen,
  onDelete,
  deleting,
  deleteError,
}: {
  conversations: ConversationSummary[]
  loading: boolean
  error: unknown
  onRetry: () => void
  current: string | null
  disabled: boolean
  onOpen: (id: string) => void
  onDelete: (id: string) => void
  deleting: boolean
  deleteError: unknown
}) {
  const [confirming, setConfirming] = useState<string | null>(null)
  return (
    <aside className={styles.history} aria-label="Your conversations">
      <span className={styles.historyTitle}>Conversations</span>
      {error ? (
        <ErrorState error={error} onRetry={onRetry} compact />
      ) : loading ? (
        <Loading label="Loading…" compact />
      ) : conversations.length === 0 ? (
        <span className={styles.historyEmpty}>Your questions are kept here, so you can come back to an answer.</span>
      ) : (
        <ul className={styles.historyList}>
          {conversations.map((c) => (
            <li key={c.id} className={`${styles.historyItem} ${c.id === current ? styles.historyOn : ''}`}>
              {confirming === c.id ? (
                <span className={styles.historyConfirm}>
                  <span>Delete this conversation?</span>
                  <span className={styles.historyConfirmActions}>
                    <Button size="sm" variant="danger" disabled={deleting} onClick={() => onDelete(c.id)}>
                      Delete
                    </Button>
                    <Button size="sm" variant="ghost" onClick={() => setConfirming(null)}>
                      Keep
                    </Button>
                  </span>
                </span>
              ) : (
                <>
                  <button
                    type="button"
                    className={styles.historyOpen}
                    disabled={disabled}
                    aria-current={c.id === current ? 'page' : undefined}
                    onClick={() => onOpen(c.id)}
                    title={c.title}
                  >
                    <span className={styles.historyName}>{c.title}</span>
                    <span className={styles.historyMeta}>
                      {timeAgo(c.updatedAt)} · {c.turns} question{c.turns === 1 ? '' : 's'}
                    </span>
                  </button>
                  <button
                    type="button"
                    className={styles.historyDelete}
                    aria-label={`Delete “${c.title}”`}
                    disabled={disabled}
                    onClick={() => setConfirming(c.id)}
                  >
                    <Trash2 size={14} aria-hidden />
                  </button>
                </>
              )}
            </li>
          ))}
        </ul>
      )}
      <InlineError error={deleteError} />
      <span className={styles.historyNote}>
        <Lock size={12} aria-hidden />
        Only you can see your conversations.
      </span>
    </aside>
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

function CitationPreview({
  selection,
  enabled,
  wide,
  onToggleWide,
}: {
  selection: Selection | null
  enabled: boolean
  wide: boolean
  onToggleWide: () => void
}) {
  if (!selection) {
    return (
      <aside className={styles.preview} aria-label="Citation preview">
        <div className={styles.previewEmpty}>
          {enabled
            ? 'Select a citation to see it marked on its page.'
            : 'Citations appear here once the assistant is available. Each one opens the cited page, and access is re-checked when you open it.'}
        </div>
      </aside>
    )
  }
  const s = selection.source
  return (
    <aside className={`${styles.preview} ${wide ? styles.previewWide : ''}`} aria-label="Citation preview">
      <div className={styles.previewHead}>
        <span className={styles.previewTitle}>
          Citation [{s.number}] · {s.title}
        </span>
        <span className={styles.previewSub}>
          {[s.documentNumber, s.documentType, `v${s.versionNumber}`, pages(s), s.section].filter(Boolean).join(' · ')}
        </span>
      </div>
      {/* Keyed by citation, so each one opens on its own page at the default zoom. */}
      <CitedDocument key={`${selection.turn}:${s.number}`} source={s} wide={wide} onToggleWide={onToggleWide} />
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

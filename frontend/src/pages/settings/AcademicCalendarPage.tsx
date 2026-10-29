import { useMemo, useState } from 'react'
import { CalendarDays, ChevronDown, ChevronRight, Info, Lock, Pencil, RefreshCw } from 'lucide-react'
import { ApiError, errorMessage } from '../../api/client'
import {
  activateAcademicTerm,
  closeAcademicTerm,
  listAcademicTermsForAdmin,
  syncCalendarFromSis,
  updateAcademicTerm,
} from '../../api/settings'
import type { AcademicTermDto, AcademicYearDto, TermStatus, UpdateAcademicTermRequest } from '../../api/types'
import { useAction, useApi } from '../../api/useApi'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Badge } from '../../components/Badge'
import type { BadgeTone } from '../../components/Badge'
import { Button } from '../../components/Button'
import { Modal } from '../../components/Modal'
import { Switch } from '../../components/Switch'
import { daysBetween, formatDate, formatDay, formatDayRange, formatRange, todayIso } from './dates'
import { Segmented } from '../../components/Segmented'
import { SettingsFrame } from './SettingsFrame'
import panel from './panel.module.css'
import styles from './AcademicCalendarPage.module.css'

// The API's Planned is "Upcoming" in the design.
const statusLabel: Record<TermStatus, string> = { Planned: 'Upcoming', Active: 'Active', Closed: 'Closed' }
const statusTone: Record<TermStatus, BadgeTone> = { Active: 'success', Planned: 'info', Closed: 'neutral' }
const statusOptions: { value: TermStatus; label: string }[] = [
  { value: 'Planned', label: 'Upcoming' },
  { value: 'Active', label: 'Active' },
  { value: 'Closed', label: 'Closed' },
]

type YearState = 'Current' | 'Planned' | 'Closed'

function yearState(year: AcademicYearDto, today: string): YearState {
  const { terms } = year
  if (terms.some((t) => t.status === 'Active')) return 'Current'
  if (terms.length > 0 && terms.every((t) => t.status === 'Closed')) return 'Closed'
  if (terms.length === 0 && year.endsOn < today) return 'Closed'
  return 'Planned'
}

type UsageKey = 'documents' | 'submissions' | 'openSubmissions'
const sum = (terms: AcademicTermDto[], key: UsageKey) => terms.reduce((total, t) => total + (t.usage?.[key] ?? 0), 0)
const n = (value: number) => value.toLocaleString('en-US')
const plural = (count: number, word: string) => `${n(count)} ${word}${count === 1 ? '' : 's'}`

/** A missing window means the whole term (AcademicCalendarService). */
const windowOf = (t: AcademicTermDto) => ({
  opens: t.submissionOpensOn ?? t.startsOn,
  closes: t.submissionClosesOn ?? t.endsOn,
})

const windowLabel = (t: AcademicTermDto) =>
  t.submissionOpensOn || t.submissionClosesOn ? formatDayRange(windowOf(t).opens, windowOf(t).closes) : 'Whole term'

export function AcademicCalendarPage() {
  const today = todayIso()
  const calendar = useApi((signal) => listAcademicTermsForAdmin(signal), [])
  // null = the default: only the current year open.
  const [expanded, setExpanded] = useState<Set<string> | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const sync = useAction(syncCalendarFromSis)

  const years = useMemo(() => calendar.data ?? [], [calendar.data])
  const terms = useMemo(() => years.flatMap((y) => y.terms), [years])

  // Current year first, then the rest newest first (frame 19).
  const orderedYears = useMemo(() => {
    const current = years.find((y) => y.terms.some((t) => t.status === 'Active'))
    const rest = years.filter((y) => y !== current).sort((a, b) => b.startsOn.localeCompare(a.startsOn))
    return current ? [current, ...rest] : rest
  }, [years])

  const active = terms.find((t) => t.status === 'Active')
  const open = expanded ?? new Set(orderedYears[0] ? [orderedYears[0].id] : [])
  const selected = terms.find((t) => t.id === selectedId) ?? active ?? terms[0]
  const selectedYear = years.find((y) => y.id === selected?.academicYearId)

  const toggleYear = (id: string) => {
    const next = new Set(open)
    if (next.has(id)) next.delete(id)
    else next.add(id)
    setExpanded(next)
  }

  /** Puts what a save returned in place (it has no usage counts), then reloads for the counts and any other term it closed. */
  const saved = (term: AcademicTermDto) => {
    calendar.setData((list) =>
      list?.map((y) => ({ ...y, terms: y.terms.map((t) => (t.id === term.id ? { ...term, usage: term.usage ?? t.usage } : t)) })),
    )
    calendar.reload()
  }

  // SIS sync isn't built yet: the API answers 501 and we show its reason.
  const syncNotice = sync.error instanceof ApiError && sync.error.status === 501 ? sync.error : null

  if (!calendar.data) {
    return (
      <SettingsFrame>
        {calendar.error ? (
          <ErrorState error={calendar.error} onRetry={calendar.reload} />
        ) : (
          <Loading label="Loading the academic calendar…" />
        )}
      </SettingsFrame>
    )
  }

  return (
    <SettingsFrame
      actions={
        <Button icon={RefreshCw} onClick={() => void sync.run()} disabled={sync.pending}>
          {sync.pending ? 'Syncing…' : 'Sync from SIS'}
        </Button>
      }
    >
      <div className={panel.body}>
        <div className={`${styles.years} ${styles.yearsScroll}`}>
          {syncNotice ? (
            <div className={styles.notice} role="status">
              <Info size={16} aria-hidden />
              {syncNotice.title} {syncNotice.detail}
            </div>
          ) : sync.error ? (
            <div className={`${styles.today} ${styles.todayWarn}`} role="alert">
              <Info size={16} aria-hidden />
              {errorMessage(sync.error)}
            </div>
          ) : null}
          {calendar.error ? <ErrorState compact error={calendar.error} onRetry={calendar.reload} /> : null}

          <TodayStrip today={today} active={active} />

          {orderedYears.map((year) => {
            const yearTerms = year.terms
            const state = yearState(year, today)
            const isOpen = open.has(year.id)
            const badge = <Badge tone={state === 'Current' ? 'success' : 'neutral'}>{state}</Badge>

            if (!isOpen) {
              return (
                <div key={year.id} className={`${styles.yearCard} ${styles.yearCollapsed}`}>
                  <button type="button" className={styles.yearHead} onClick={() => toggleYear(year.id)} aria-expanded={false}>
                    <ChevronRight size={16} className={styles.chevron} aria-hidden />
                    <span className={styles.yearName}>{year.name}</span>
                    {badge}
                    <span className={styles.sync}>
                      {plural(yearTerms.length, 'term')} · {plural(sum(yearTerms, 'documents'), 'document')} ·{' '}
                      {plural(sum(yearTerms, 'openSubmissions'), 'open submission')}
                    </span>
                  </button>
                </div>
              )
            }

            return (
              <div key={year.id} className={styles.yearCard}>
                <button type="button" className={styles.yearHead} onClick={() => toggleYear(year.id)} aria-expanded>
                  <ChevronDown size={16} className={styles.chevron} aria-hidden />
                  <span className={styles.yearTitle}>
                    <span className={styles.yearTitleRow}>
                      <span className={styles.yearName}>{year.name}</span>
                      {badge}
                    </span>
                    <span className={styles.yearMeta}>
                      {formatDate(year.startsOn)} – {formatDate(year.endsOn)} · code {year.code} ·{' '}
                      {plural(yearTerms.length, 'term')}
                    </span>
                  </span>
                  <span className={styles.sync}>
                    {year.fromSis ? <RefreshCw size={13} aria-hidden /> : <Pencil size={13} aria-hidden />}
                    {year.fromSis ? 'From SIS' : 'Added by hand'}
                  </span>
                </button>

                <div className={styles.terms}>
                  <div className={`${panel.headRow} ${styles.termHead}`}>
                    <span className={`${panel.th} ${panel.fill}`}>Term</span>
                    <span className={`${panel.th} ${styles.colCode}`}>Code</span>
                    <span className={`${panel.th} ${styles.colDates}`}>Dates</span>
                    <span className={`${panel.th} ${styles.colWindow}`}>Submission window</span>
                    <span className={`${panel.th} ${styles.colStatus}`}>Status</span>
                    <span className={`${panel.th} ${styles.colTagged}`}>Tagged</span>
                  </div>
                  {yearTerms.map((t) => (
                    <button
                      key={t.id}
                      type="button"
                      className={`${styles.termRow} ${t.id === selected?.id ? styles.termSelected : ''}`}
                      aria-pressed={t.id === selected?.id}
                      onClick={() => setSelectedId(t.id)}
                    >
                      <span className={`${styles.termName} ${panel.fill}`}>{t.name}</span>
                      <span className={`${panel.cell} ${panel.muted} ${styles.colCode}`}>{t.code}</span>
                      <span className={`${panel.cell} ${styles.colDates}`}>{formatRange(t.startsOn, t.endsOn)}</span>
                      <span className={`${panel.cell} ${styles.colWindow} ${t.submissionWindowOpen ? styles.windowOpen : ''}`}>
                        {windowLabel(t)}
                      </span>
                      <span className={`${panel.cell} ${styles.colStatus}`}>
                        <Badge tone={statusTone[t.status]}>{statusLabel[t.status]}</Badge>
                      </span>
                      <span className={`${panel.cell} ${styles.colTagged} ${styles.tagged}`}>
                        {t.usage ? (
                          <>
                            <span className={t.usage.documents ? '' : panel.muted}>{plural(t.usage.documents, 'document')}</span>
                            <span className={styles.taggedSub}>
                              {t.usage.openSubmissions
                                ? plural(t.usage.openSubmissions, 'open submission')
                                : t.usage.submissions
                                  ? plural(t.usage.submissions, 'submission')
                                  : '—'}
                            </span>
                          </>
                        ) : (
                          <span className={panel.muted}>—</span>
                        )}
                      </span>
                    </button>
                  ))}
                  {yearTerms.length === 0 && (
                    <div className={styles.noTerms}>No terms yet. They arrive with the SIS data.</div>
                  )}
                </div>
              </div>
            )
          })}
          {orderedYears.length === 0 && (
            <div className={`${styles.today} ${styles.todayWarn}`}>No academic years yet. They arrive with the SIS data.</div>
          )}

          <div className={styles.usage}>
            <span className={panel.eyebrow}>Where year and term are used</span>
            <span>
              Document tag (required by its type) · Submission term (required, defaults to the active term) · Requirement
              set effective term · Library and review queue filters
            </span>
          </div>
        </div>

        {selected && selectedYear && (
          <TermDrawer
            key={selected.id}
            term={selected}
            year={selectedYear}
            terms={terms}
            onSaved={saved}
            onReload={calendar.reload}
          />
        )}
      </div>
    </SettingsFrame>
  )
}

function TodayStrip({ today, active }: { today: string; active?: AcademicTermDto }) {
  if (!active) {
    return (
      <div className={`${styles.today} ${styles.todayWarn}`}>
        <CalendarDays size={16} aria-hidden />
        Today, {formatDate(today)} · No active term. New uploads and submissions have no default term until you set one.
      </div>
    )
  }
  const { opens, closes } = windowOf(active)
  let windowText: string
  if (today < opens) {
    windowText = `its submission window opens in ${plural(daysBetween(today, opens), 'day')} (${formatDay(opens)})`
  } else if (today <= closes) {
    const days = daysBetween(today, closes)
    windowText =
      days === 0
        ? 'its submission window closes today'
        : `its submission window closes in ${plural(days, 'day')} (${formatDay(closes)})`
  } else {
    windowText = `its submission window closed on ${formatDay(closes)}`
  }
  return (
    <div className={styles.today}>
      <CalendarDays size={16} aria-hidden />
      Today, {formatDate(today)} · Active term: {active.name} {active.academicYearName} · {windowText}
    </div>
  )
}

interface Draft {
  opens: string
  closes: string
  allowResubmit: boolean
  status: TermStatus
}

const toDraft = (t: AcademicTermDto): Draft => ({
  opens: t.submissionOpensOn ?? '',
  closes: t.submissionClosesOn ?? '',
  allowResubmit: t.allowResubmitAfterClose,
  status: t.status,
})

/** The PATCH body for the window and resubmit switch, or null when they didn't change. */
function windowChanges(term: AcademicTermDto, draft: Draft): UpdateAcademicTermRequest | null {
  const before = toDraft(term)
  const windowChanged = draft.opens !== before.opens || draft.closes !== before.closes
  const clear = windowChanged && !draft.opens && !draft.closes
  const body: UpdateAcademicTermRequest = {
    submissionOpensOn: windowChanged && !clear && draft.opens !== before.opens ? draft.opens : null,
    submissionClosesOn: windowChanged && !clear && draft.closes !== before.closes ? draft.closes : null,
    allowResubmitAfterClose: draft.allowResubmit !== before.allowResubmit ? draft.allowResubmit : null,
    clearSubmissionWindow: clear ? true : null,
  }
  return Object.values(body).some((v) => v !== null) ? body : null
}

interface TermDrawerProps {
  term: AcademicTermDto
  year: AcademicYearDto
  terms: AcademicTermDto[]
  onSaved: (term: AcademicTermDto) => void
  onReload: () => void
}

function TermDrawer({ term, year, terms, onSaved, onReload }: TermDrawerProps) {
  const [draft, setDraft] = useState(() => toDraft(term))
  const [confirmClose, setConfirmClose] = useState(false)
  const closed = term.status === 'Closed'

  const save = useAction(async (patch: UpdateAcademicTermRequest | null, status: TermStatus) => {
    // The window is saved first; the status change then uses the etag that save returned.
    let current = term
    if (patch) {
      current = await updateAcademicTerm(term.id, term.etag, patch)
      onSaved(current)
    }
    if (status === 'Active' && current.status !== 'Active') {
      current = await activateAcademicTerm(term.id, current.etag)
      onSaved(current)
    } else if (status === 'Closed' && current.status === 'Active') {
      current = await closeAcademicTerm(term.id, current.etag)
      onSaved(current)
    }
    return current
  })

  const patch = windowChanges(term, draft)
  const statusChanged = draft.status !== term.status
  const dirty = Boolean(patch) || statusChanged

  const windowError =
    Boolean(draft.opens) !== Boolean(draft.closes)
      ? 'Set both dates, or clear both to use the whole term.'
      : draft.opens && draft.opens > draft.closes
        ? 'The window must open before it closes.'
        : draft.opens && (draft.opens < term.startsOn || draft.closes > term.endsOn)
          ? `Must fall inside the term’s dates (${formatRange(term.startsOn, term.endsOn)}).`
          : null

  const currentActive = terms.find((t) => t.status === 'Active' && t.id !== term.id)
  const nextUp = terms
    .filter((t) => t.status === 'Planned' && t.startsOn > term.startsOn)
    .sort((a, b) => a.startsOn.localeCompare(b.startsOn))[0]

  // Only Upcoming → Active and Active → Closed exist (AcademicCalendarService); other picks can't be saved.
  let statusError: string | null = null
  let statusHelp: string
  if (closed) {
    statusHelp = 'A closed term can’t become active again.'
  } else if (draft.status === 'Planned' && term.status === 'Active') {
    statusError = 'An active term can’t go back to Upcoming. Close it, or activate the next term.'
    statusHelp = ''
  } else if (draft.status === 'Closed' && term.status === 'Planned') {
    statusError = 'Only the active term can be closed.'
    statusHelp = ''
  } else if (draft.status === 'Active' && term.status === 'Planned') {
    if (currentActive && currentActive.startsOn > term.startsOn) {
      statusError = `${currentActive.name} ${currentActive.academicYearName} is active and starts after this term; activate terms in calendar order.`
    }
    statusHelp = currentActive
      ? `Only one term is Active at a time. Saving closes ${currentActive.name} ${currentActive.code} and makes this term the default for new uploads and submissions.`
      : 'Saving makes this term the default for new uploads and submissions.'
  } else if (draft.status === 'Active') {
    statusHelp = nextUp
      ? `Only one term is Active at a time. Activating ${nextUp.name} ${nextUp.code} later closes this one.`
      : 'Only one term is Active at a time. It is the default for new uploads and submissions.'
  } else if (term.status === 'Active') {
    statusHelp = 'No term will be Active after saving, so new uploads and submissions get no default term.'
  } else {
    statusHelp = 'Only one term is Active at a time.'
  }

  const statusLine =
    term.status === 'Active'
      ? `Active · until ${formatDate(term.endsOn)}`
      : term.status === 'Planned'
        ? `Upcoming · starts ${formatDate(term.startsOn)}`
        : 'Closed'

  const submit = async () => {
    if (!dirty || windowError || statusError) return
    const next = await save.run(patch, draft.status)
    if (next) setDraft(toDraft(next))
  }

  const closeTerm = async () => {
    const next = await save.run(null, 'Closed')
    if (next) {
      setDraft(toDraft(next))
      setConfirmClose(false)
    }
  }

  const stale = save.error instanceof ApiError && save.error.isStale
  const usage = term.usage

  return (
    <aside className={panel.drawer} aria-label="Edit term">
      <div className={panel.drawerHead}>
        <span className={panel.eyebrow}>Edit term</span>
        <span className={panel.drawerTitle}>
          {term.name} · {year.name}
        </span>
        <span className={panel.drawerSub}>
          {term.code} · {statusLine}
        </span>
      </div>
      <div className={panel.rule} />

      <div className={`${panel.form} ${styles.drawerScroll}`}>
        <div className={panel.box} style={{ gap: 8 }}>
          <span className={panel.boxLabel}>
            {term.fromSis ? <Lock size={12} aria-hidden /> : <Pencil size={12} aria-hidden />}
            {term.fromSis ? 'From SIS · read-only here' : 'Added by hand · read-only here'}
          </span>
          <span className={styles.sisLine}>
            <span>
              <span className={styles.sisKey}>Name</span>
              {term.name}
            </span>
            <span>
              <span className={styles.sisKey}>Code</span>
              {term.code}
            </span>
          </span>
          <span className={styles.sisLine}>
            <span>
              <span className={styles.sisKey}>Dates</span>
              {formatDate(term.startsOn)} – {formatDate(term.endsOn)}
            </span>
          </span>
        </div>

        <div className={panel.field}>
          <span className={panel.label}>Submission window</span>
          <div className={styles.dateInputs}>
            <label className={`${styles.dateField} ${windowError ? styles.dateInvalid : ''}`}>
              <CalendarDays size={13} aria-hidden />
              <input
                type="date"
                aria-label="Window opens"
                value={draft.opens}
                min={term.startsOn}
                max={term.endsOn}
                disabled={closed}
                onChange={(e) => setDraft((d) => ({ ...d, opens: e.target.value }))}
              />
            </label>
            to
            <label className={`${styles.dateField} ${windowError ? styles.dateInvalid : ''}`}>
              <CalendarDays size={13} aria-hidden />
              <input
                type="date"
                aria-label="Window closes"
                value={draft.closes}
                min={term.startsOn}
                max={term.endsOn}
                disabled={closed}
                onChange={(e) => setDraft((d) => ({ ...d, closes: e.target.value }))}
              />
            </label>
          </div>
          {windowError ? (
            <span className={panel.error}>{windowError}</span>
          ) : (
            <span className={panel.help}>
              {closed
                ? 'The window of a closed term can’t change.'
                : 'Must fall inside the term’s dates; leave both empty to use the whole term. Faculty can submit only while it is open; reviewers can act after it closes.'}
            </span>
          )}
        </div>

        <div className={panel.toggleRow}>
          <Switch
            checked={draft.allowResubmit}
            onChange={(v) => setDraft((d) => ({ ...d, allowResubmit: v }))}
            label="Allow resubmits after the window closes"
          />
          <span className={panel.toggleText}>
            <span className={panel.toggleTitle}>Allow resubmits after the window closes</span>
            <span className={panel.muted}>Only for submissions returned with “Revision requested”.</span>
          </span>
        </div>

        <div className={panel.field}>
          <span className={panel.label}>Status</span>
          <Segmented
            options={statusOptions}
            value={draft.status}
            onChange={(v) => setDraft((d) => ({ ...d, status: v }))}
            disabled={closed}
            ariaLabel="Term status"
          />
          {statusError ? (
            <span className={panel.error}>{statusError}</span>
          ) : (
            <span className={term.status === 'Active' && draft.status !== 'Active' ? panel.warn : panel.help}>
              {statusHelp}
            </span>
          )}
        </div>

        <div className={panel.box}>
          <span className={panel.boxLabel}>Tagged with this term</span>
          {usage ? (
            <>
              <span className={panel.boxLine}>
                {plural(usage.documents, 'document')} · {plural(usage.submissions, 'submission')} ({n(usage.openSubmissions)}{' '}
                open, {n(usage.approvedSubmissions)} approved)
              </span>
              <span className={panel.boxLine}>
                Requirement sets in force: {n(usage.requirementSets)} (per unit and submission type)
              </span>
            </>
          ) : (
            <span className={`${panel.boxLine} ${panel.muted}`}>Counts appear after the list reloads.</span>
          )}
        </div>

        {save.error ? (
          <div className={styles.actionError}>
            <InlineError error={save.error} />
            {stale && (
              <Button size="sm" onClick={onReload}>
                Reload this term
              </Button>
            )}
          </div>
        ) : null}
      </div>

      <div className={panel.drawerFooter}>
        <Button onClick={() => setConfirmClose(true)} disabled={term.status !== 'Active' || save.pending}>
          Close term…
        </Button>
        <Button
          variant="primary"
          disabled={!dirty || Boolean(windowError) || Boolean(statusError) || save.pending}
          onClick={() => void submit()}
        >
          {save.pending ? 'Saving…' : 'Save changes'}
        </Button>
      </div>

      {confirmClose && (
        <Modal
          title={`Close ${term.name} ${year.name}?`}
          onClose={() => setConfirmClose(false)}
          footer={
            <>
              <Button onClick={() => setConfirmClose(false)}>Cancel</Button>
              <Button variant="danger" onClick={() => void closeTerm()} disabled={save.pending}>
                {save.pending ? 'Closing…' : 'Close term'}
              </Button>
            </>
          }
        >
          <p className={panel.help}>
            Closing freezes new submissions for this term, and no term is active until you activate the next one. Its{' '}
            {plural(usage?.documents ?? 0, 'document')} keep their tags and stay searchable.
          </p>
          {usage && usage.openSubmissions > 0 && (
            <p className={panel.warn}>
              {plural(usage.openSubmissions, 'submission')} {usage.openSubmissions === 1 ? 'is' : 'are'} still open.
              Reviewers can still act on them.
            </p>
          )}
          <InlineError error={save.error} />
        </Modal>
      )}
    </aside>
  )
}

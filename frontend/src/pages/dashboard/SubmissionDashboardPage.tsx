import { Fragment } from 'react'
import type { ReactNode } from 'react'
import { ChevronRight, Clock, Download, Inbox, LayoutDashboard } from 'lucide-react'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { getAcademicYears } from '../../api/reference'
import type { DashboardLevel, DashboardRow, DashboardScopeRef, ReviewActionType, StatusCounts, SubmissionDashboard } from '../../api/types'
import { useApi } from '../../api/useApi'
import { getSubmissionDashboard } from '../../api/workflow'
import { ErrorState, Loading } from '../../components/AsyncState'
import { Badge } from '../../components/Badge'
import { Button } from '../../components/Button'
import { downloadCsv } from '../../lib/csv'
import { shortDate, termShortLabel, timeAgo } from '../../lib/format'
import { shortName } from '../review/reviewModel'
import styles from './SubmissionDashboardPage.module.css'

// One page at three levels: all colleges (VPAF, admin) → a college (its Dean) → a program (its AS); and for offices like
// CICT, all non-academic departments (VPAF, admin) → a department (its Head). ?college=<id>, ?program=<id>,
// ?view=non-academic or ?department=<id> picks the scope, ?term=<id> the term; the API checks what the caller may open.

/** Where submissions are, in reading order. Colours live in the CSS module (validated for colour-blind separation). */
const statusParts = [
  { key: 'approved', label: 'Approved', count: (c: StatusCounts) => c.approved },
  { key: 'underReview', label: 'Under review', count: (c: StatusCounts) => c.underReview },
  { key: 'waiting', label: 'Waiting for review', count: (c: StatusCounts) => c.submitted + c.resubmitted },
  { key: 'revision', label: 'Needs revision', count: (c: StatusCounts) => c.revisionRequested },
  { key: 'rejected', label: 'Rejected', count: (c: StatusCounts) => c.rejected },
] as const

const actionLabels: Partial<Record<ReviewActionType, string>> = {
  Submitted: 'submitted',
  Opened: 'opened',
  Commented: 'commented on',
  Approved: 'approved',
  RevisionRequested: 'asked for a revision of',
  Rejected: 'rejected',
  Resubmitted: 'resubmitted',
  Withdrawn: 'withdrew',
  Published: 'published',
  Reassigned: 'reassigned',
}

const rowNouns = { University: 'Colleges', College: 'Programs', Program: 'Faculty', NonAcademic: 'Departments', Department: 'Staff' } as const
/** The first column's heading at each level. */
const rowHeads = { University: 'College', College: 'Program', Program: 'Person', NonAcademic: 'Department', Department: 'Person' } as const
/** The level a row opens, and the hint above the table. */
const childLevel: Partial<Record<DashboardLevel, DashboardLevel>> = { University: 'College', College: 'Program', NonAcademic: 'Department' }
const openHints: Partial<Record<DashboardLevel, string>> = {
  University: 'Open a college to see its programs',
  College: 'Open a program to see its faculty',
  NonAcademic: 'Open a department to see its staff',
}
/** Who leads a scope (the subtitle) and each row of its table: a college's Dean, a program's AS, a department's Head. */
const scopeLeads: Partial<Record<DashboardLevel, string>> = { College: 'Dean', Program: 'AS', Department: 'Head' }
const rowLeads: Partial<Record<DashboardLevel, string>> = { University: 'Dean', College: 'AS', NonAcademic: 'Head' }

const isOffice = (level: DashboardLevel) => level === 'NonAcademic' || level === 'Department'
const isPeople = (level: DashboardLevel) => level === 'Program' || level === 'Department'
/** Roots have no id: the colleges and the non-academic departments. */
const scopeKey = (scope: DashboardScopeRef) => `${scope.level}:${scope.id ?? ''}`

const avgLabel = (days: number | null) => (days === null ? '—' : `${days.toFixed(1)} d`)
const percent = (part: number, whole: number) => (whole === 0 ? 0 : Math.round((100 * part) / whole))

export function SubmissionDashboardPage() {
  const [params] = useSearchParams()
  const collegeId = params.get('college') ?? undefined
  const programId = params.get('program') ?? undefined
  const departmentId = params.get('department') ?? undefined
  const nonAcademic = params.get('view') === 'non-academic' || undefined
  const termId = params.get('term') ?? undefined

  const dashboard = useApi(
    (signal) => getSubmissionDashboard({ collegeId, programId, departmentId, nonAcademic, termId }, signal),
    [collegeId, programId, departmentId, nonAcademic, termId],
  )
  const years = useApi(() => getAcademicYears(), [])

  if (!dashboard.data) {
    return (
      <>
        <Topbar title="Submissions dashboard" />
        {dashboard.error ? (
          <ErrorState error={dashboard.error} onRetry={dashboard.reload} />
        ) : (
          <Loading label="Loading the dashboard…" />
        )}
      </>
    )
  }

  const terms = (years.data ?? []).flatMap((y) => y.terms).filter((t) => t.status !== 'Planned' || t.id === dashboard.data?.term.id)
  return <DashboardBody data={dashboard.data} terms={terms} loading={dashboard.loading} />
}

interface TopbarProps {
  title: string
  trail?: DashboardScopeRef[]
  hrefOf?: (scope: DashboardScopeRef) => string
  actions?: ReactNode
}

function Topbar({ title, trail = [], hrefOf, actions }: TopbarProps) {
  return (
    <header className={styles.topbar}>
      <div className={styles.titleBlock}>
        <nav className={styles.breadcrumb} aria-label="Dashboard level">
          <span>Dashboard</span>
          {trail.map((scope, i) => (
            <Fragment key={scopeKey(scope)}>
              <ChevronRight size={13} aria-hidden />
              {i < trail.length - 1 && hrefOf ? (
                <Link to={hrefOf(scope)}>{scope.id ? scope.code : scope.name}</Link>
              ) : (
                <span aria-current="page">{scope.id ? scope.code : scope.name}</span>
              )}
            </Fragment>
          ))}
        </nav>
        <div className={styles.titleRow}>
          <LayoutDashboard size={24} aria-hidden className={styles.titleIcon} />
          <h1 className={styles.title}>{title}</h1>
        </div>
      </div>
      {actions && <div className={styles.actions}>{actions}</div>}
    </header>
  )
}

interface DashboardBodyProps {
  data: SubmissionDashboard
  terms: { id: string; shortName: string; academicYearName: string }[]
  loading: boolean
}

function DashboardBody({ data, terms, loading }: DashboardBodyProps) {
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const { totals, level } = data
  const termText = termShortLabel({ shortName: data.term.shortName, academicYearName: data.term.yearName })

  /** Links keep the picked term. */
  const hrefOf = (scope: DashboardScopeRef) => {
    const next = new URLSearchParams()
    if (scope.level === 'College' && scope.id) next.set('college', scope.id)
    if (scope.level === 'Program' && scope.id) next.set('program', scope.id)
    if (scope.level === 'Department' && scope.id) next.set('department', scope.id)
    if (scope.level === 'NonAcademic') next.set('view', 'non-academic')
    const term = params.get('term')
    if (term) next.set('term', term)
    const query = next.toString()
    return query ? `/dashboard?${query}` : '/dashboard'
  }
  const drill = (row: DashboardRow) => {
    const child = childLevel[level]
    return child ? hrefOf({ level: child, id: row.id, code: row.code, name: row.name }) : undefined
  }
  const office = isOffice(level)
  const members = office ? 'Staff' : 'Faculty'

  const setTerm = (id: string) => {
    const next = new URLSearchParams(params)
    next.set('term', id)
    navigate(`/dashboard?${next.toString()}`)
  }

  const exportCsv = () => {
    downloadCsv(`${data.scope.code}-${termText.replace(/\s+/g, '-')}-submissions.csv`, [
      [rowHeads[level], 'Code', 'Lead', members, `${members} submitted`, 'Courses expected', 'Courses submitted', 'Submissions', 'Open',
        'Overdue', 'Avg days to decision', 'Approved', 'Under review', 'Waiting', 'Needs revision', 'Rejected'],
      ...data.rows.map((r) => [
        r.name, r.code, r.lead?.name ?? '', r.facultyCount, r.facultySubmitted, r.courseExpected, r.courseSubmitted, r.submissions,
        r.open, r.overdue,
        r.avgDaysToDecision ?? '', r.byStatus.approved, r.byStatus.underReview, r.byStatus.submitted + r.byStatus.resubmitted,
        r.byStatus.revisionRequested, r.byStatus.rejected,
      ]),
    ])
  }

  const pickScope = (key: string) => {
    const scope = data.options.find((o) => scopeKey(o) === key)
    if (scope) navigate(hrefOf(scope))
  }
  const termOptions = terms.some((t) => t.id === data.term.id)
    ? terms
    : [{ id: data.term.id, shortName: data.term.shortName, academicYearName: data.term.yearName }, ...terms]

  const actions = (
    <>
      {data.options.length > 1 && (
        <label>
          <span className="visually-hidden">College, program or department</span>
          <select className="field" value={scopeKey(data.scope)} onChange={(e) => pickScope(e.target.value)}>
            {data.options.map((o) => (
              <option key={scopeKey(o)} value={scopeKey(o)}>
                {o.name}
              </option>
            ))}
          </select>
        </label>
      )}
      <label>
        <span className="visually-hidden">Term</span>
        <select className="field" value={data.term.id} onChange={(e) => setTerm(e.target.value)}>
          {termOptions.map((t) => (
            <option key={t.id} value={t.id}>
              {termShortLabel(t)}
            </option>
          ))}
        </select>
      </label>
      <Button onClick={exportCsv} disabled={data.rows.length === 0}>
        <Download size={16} aria-hidden /> Export CSV
      </Button>
    </>
  )

  const notSubmitted = totals.facultyCount - totals.facultySubmitted
  // Per-course types (syllabi, lab manuals) are expected once per course taught in the term's load.
  const perCourse = totals.courseExpected > 0
  const scopeLead = totals.lead ? `${scopeLeads[level]} ${totals.lead.name}` : null
  const title =
    level === 'University'
      ? 'Submissions · all colleges'
      : level === 'NonAcademic'
        ? 'Submissions · non-academic departments'
        : `Submissions · ${data.scope.name}`

  return (
    <div className={`${styles.page} ${loading ? styles.stale : ''}`}>
      <Topbar title={title} trail={data.trail} hrefOf={hrefOf} actions={actions} />
      <p className={styles.subtitle}>
        {termText}
        {scopeLead ? ` · ${scopeLead}` : ''}
        {level === 'University' ? ` · ${data.rows.length} ${data.rows.length === 1 ? 'college' : 'colleges'}` : ''}
        {level === 'NonAcademic' ? ` · ${data.rows.length} ${data.rows.length === 1 ? 'department' : 'departments'}` : ''}
      </p>

      <div className={`${styles.kpis} ${perCourse ? styles.kpisSix : ''}`}>
        <Kpi
          label={`${members} who submitted`}
          value={`${totals.compliancePercent}%`}
          note={
            totals.facultyCount === 0
              ? `No ${members.toLowerCase()} assigned`
              : `${totals.facultySubmitted} of ${totals.facultyCount}${notSubmitted > 0 ? ` · ${notSubmitted} not yet` : ''}`
          }
        />
        {perCourse && (
          <Kpi
            label="Courses covered"
            value={`${percent(totals.courseSubmitted, totals.courseExpected)}%`}
            note={`${totals.courseSubmitted} of ${totals.courseExpected} per-course submissions`}
          />
        )}
        <Kpi label="Open reviews" value={totals.open} note={`${totals.submissions} submitted this term`} />
        <Kpi
          label="Overdue (over 5 days)"
          value={totals.overdue}
          tone={totals.overdue > 0 ? 'danger' : undefined}
          note={totals.overdue > 0 ? 'Waiting on a reviewer' : 'Nothing is overdue'}
        />
        <Kpi label="Avg days to decision" value={avgLabel(totals.avgDaysToDecision)} note="From assignment to decision" />
        <Kpi
          label="Approved this term"
          value={totals.published}
          tone={totals.published > 0 ? 'success' : undefined}
          note={`${totals.publishedLast7} in the last 7 days`}
        />
      </div>

      <section className={styles.card} aria-labelledby="status-title">
        <div className={styles.cardHead}>
          <h2 id="status-title" className={styles.cardTitle}>Where this term&apos;s submissions are</h2>
          <span className={styles.cardSub}>{totals.submissions} submissions, drafts not counted</span>
        </div>
        <div className={styles.statusBody}>
          {totals.submissions === 0 ? (
            <span className={styles.empty}><Inbox size={16} aria-hidden /> No submissions yet this term.</span>
          ) : (
            <>
              <StatusBar counts={totals.byStatus} label="All submissions" />
              <ul className={styles.legend}>
                {statusParts.map((p) => {
                  const value = p.count(totals.byStatus)
                  return (
                    <li key={p.key} className={styles.legendItem}>
                      <span className={`${styles.swatch} ${styles[p.key]}`} aria-hidden />
                      <span className={styles.legendLabel}>{p.label}</span>
                      <span className={styles.legendValue}>{value}</span>
                      <span className={styles.legendShare}>{percent(value, totals.submissions)}%</span>
                    </li>
                  )
                })}
              </ul>
            </>
          )}
        </div>
      </section>

      <div className={styles.grid}>
        <section className={styles.card} aria-labelledby="rows-title">
          <div className={styles.cardHead}>
            <h2 id="rows-title" className={styles.cardTitle}>{rowNouns[level]}</h2>
            <span className={styles.cardSub}>
              {isPeople(level) ? 'Each person’s submissions this term; people who haven’t submitted come first' : openHints[level]}
            </span>
          </div>
          {data.rows.length === 0 ? (
            <span className={styles.empty}><Inbox size={16} aria-hidden /> Nothing to show for this term.</span>
          ) : (
            <BreakdownTable data={data} drill={drill} />
          )}
        </section>

        <div className={styles.side}>
          <section className={styles.card} aria-labelledby="types-title">
            <div className={styles.cardHead}>
              <h2 id="types-title" className={styles.cardTitle}>By submission type</h2>
            </div>
            {data.byType.length === 0 ? (
              <span className={styles.empty}>No submissions yet.</span>
            ) : (
              <ul className={styles.list}>
                {data.byType.map((t) => (
                  <li key={t.type.id} className={styles.typeItem}>
                    <div className={styles.typeHead}>
                      <span className={styles.itemTitle}>{t.type.name}</span>
                      <span className={styles.itemMeta}>
                        {t.submissions} · {t.open} open{t.overdue > 0 ? ` · ${t.overdue} overdue` : ''}
                      </span>
                    </div>
                    <StatusBar counts={t.byStatus} label={t.type.name} small />
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section className={styles.card} aria-labelledby="overdue-title">
            <div className={styles.cardHead}>
              <h2 id="overdue-title" className={styles.cardTitle}>
                <Clock size={16} aria-hidden className={styles.danger} /> Overdue ({data.overdue.length})
              </h2>
              <span className={styles.cardSub}>Over 5 days with a reviewer, oldest first</span>
            </div>
            {data.overdue.length === 0 ? (
              <span className={styles.empty}>Nothing is overdue.</span>
            ) : (
              <ul className={styles.list}>
                {data.overdue.slice(0, 8).map((o) => (
                  <li key={o.submissionId}>
                    <Link className={styles.item} to={`/review?id=${o.submissionId}`}>
                      <span className={styles.itemTitle}>{o.title}</span>
                      <span className={styles.itemMeta}>
                        {o.number} · {o.unitCode} · {shortName(o.submitter.name)} ·{' '}
                        {o.unitHeadVacant
                          ? office ? 'no head' : 'no AS'
                          : o.reviewerIsYou ? 'with you' : `with ${o.reviewer ? shortName(o.reviewer.name) : 'nobody'}`}
                      </span>
                      <Badge tone="danger">{o.ageDays} d</Badge>
                    </Link>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section className={styles.card} aria-labelledby="activity-title">
            <div className={styles.cardHead}>
              <h2 id="activity-title" className={styles.cardTitle}>Recent activity</h2>
            </div>
            {data.recentActivity.length === 0 ? (
              <span className={styles.empty}>No activity yet this term.</span>
            ) : (
              <ul className={styles.list}>
                {data.recentActivity.map((a, i) => (
                  <li key={`${a.submissionId}-${i}`} className={styles.activity}>
                    <span>
                      <strong>{shortName(a.actor.name)}</strong> {actionLabels[a.action] ?? a.action.toLowerCase()}{' '}
                      <Link to={`/review?id=${a.submissionId}`}>{a.title}</Link>
                    </span>
                    <span className={styles.itemMeta}>
                      {a.unitCode} · {timeAgo(a.at)}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </div>
      </div>
    </div>
  )
}

interface KpiProps {
  label: string
  value: string | number
  note: string
  tone?: 'danger' | 'success'
}

function Kpi({ label, value, note, tone }: KpiProps) {
  return (
    <div className={styles.kpi}>
      <span className={styles.kpiLabel}>{label}</span>
      <span className={`${styles.kpiValue} ${tone ? styles[tone] : ''}`}>{value}</span>
      <span className={styles.kpiNote}>{note}</span>
    </div>
  )
}

/** Part-to-whole bar of a scope's submissions by status; each segment shows its count on hover. */
function StatusBar({ counts, label, small = false }: { counts: StatusCounts; label: string; small?: boolean }) {
  const parts = statusParts.map((p) => ({ key: p.key, label: p.label, value: p.count(counts) })).filter((p) => p.value > 0)
  const total = parts.reduce((n, p) => n + p.value, 0)
  const summary = total === 0 ? 'no submissions' : parts.map((p) => `${p.label} ${p.value}`).join(', ')
  return (
    <div className={`${styles.bar} ${small ? styles.barSmall : ''}`} role="img" aria-label={`${label}: ${summary}`}>
      {total === 0 ? (
        <span className={styles.barEmpty} />
      ) : (
        parts.map((p) => (
          <span key={p.key} className={`${styles.segment} ${styles[p.key]}`} style={{ flexGrow: p.value }}>
            <span className={styles.tip}>
              {p.label} · {p.value} ({percent(p.value, total)}%)
            </span>
          </span>
        ))
      )}
    </div>
  )
}

function BreakdownTable({ data, drill }: { data: SubmissionDashboard; drill: (row: DashboardRow) => string | undefined }) {
  const people = isPeople(data.level)
  const members = isOffice(data.level) ? 'staff' : 'faculty'
  const rowLead = rowLeads[data.level]
  const courses = data.totals.courseExpected > 0
  const columns = 6 + (people ? 0 : 1) + (courses ? 1 : 0)
  return (
    <div className={styles.tableWrap}>
      <table className={styles.table}>
        <thead>
          <tr>
            <th scope="col">{rowHeads[data.level]}</th>
            {!people && <th scope="col">{isOffice(data.level) ? 'Staff' : 'Faculty'} submitted</th>}
            {courses && <th scope="col">Courses covered</th>}
            <th scope="col" className={styles.num}>Submitted</th>
            <th scope="col" className={styles.num}>Open</th>
            <th scope="col" className={styles.num}>Overdue</th>
            {people ? (
              <th scope="col">Last submitted</th>
            ) : (
              <th scope="col" className={styles.num}>Avg days</th>
            )}
            <th scope="col" className={styles.statusCol}>Status</th>
          </tr>
        </thead>
        <tbody>
          {data.rows.map((row, i) => {
            const href = drill(row)
            // Programs are ordered by department: a header row starts each one.
            const header = data.level === 'College' && row.group && row.group !== data.rows[i - 1]?.group
            return (
              <Fragment key={row.id}>
                {header && (
                  <tr className={styles.groupRow}>
                    <th scope="colgroup" colSpan={columns}>
                      {row.group}
                    </th>
                  </tr>
                )}
                <tr>
                  <th scope="row" className={styles.nameCell}>
                    {href ? (
                      <Link to={href} className={styles.drill}>
                        <span className={styles.code}>{row.code}</span> {row.name}
                        <ChevronRight size={14} aria-hidden />
                      </Link>
                    ) : (
                      <span className={styles.person}>
                        {row.name}
                        {row.facultyCount > 0 && row.submissions === 0 && <Badge tone="warning">Not submitted</Badge>}
                      </span>
                    )}
                    <span className={styles.lead}>
                      {people ? row.code : row.lead ? `${rowLead} · ${row.lead.name}` : `No ${rowLead} assigned`}
                    </span>
                  </th>
                  {!people && (
                    <td>
                      <span className={styles.meter} aria-hidden>
                        <span className={styles.meterFill} style={{ width: `${row.compliancePercent}%` }} />
                      </span>
                      <span className={styles.meterText}>
                        {row.facultyCount === 0
                          ? `No ${members} yet`
                          : `${row.facultySubmitted}/${row.facultyCount} · ${row.compliancePercent}%`}
                      </span>
                    </td>
                  )}
                  {courses && (
                    <td>
                      {row.courseExpected === 0 ? (
                        <span className={styles.meterText}>{people ? 'No courses' : 'None expected'}</span>
                      ) : (
                        <>
                          <span className={styles.meter} aria-hidden>
                            <span
                              className={styles.meterFill}
                              style={{ width: `${percent(row.courseSubmitted, row.courseExpected)}%` }}
                            />
                          </span>
                          <span className={styles.meterText}>
                            {row.courseSubmitted}/{row.courseExpected} · {percent(row.courseSubmitted, row.courseExpected)}%
                          </span>
                        </>
                      )}
                    </td>
                  )}
                  <td className={styles.num}>{row.submissions}</td>
                  <td className={styles.num}>{row.open}</td>
                  <td className={`${styles.num} ${row.overdue > 0 ? styles.danger : ''}`}>{row.overdue}</td>
                  {people ? (
                    <td>{row.lastSubmittedAt ? shortDate(row.lastSubmittedAt) : '—'}</td>
                  ) : (
                    <td className={styles.num}>{avgLabel(row.avgDaysToDecision)}</td>
                  )}
                  <td className={styles.statusCol}>
                    <StatusBar counts={row.byStatus} label={row.name} small />
                  </td>
                </tr>
              </Fragment>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

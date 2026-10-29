// Date helpers for ISO 'YYYY-MM-DD' strings. Parsed as UTC so dates never shift with the time zone.

const DAY = 86_400_000

export const parseIso = (iso: string) => {
  const [y, m, d] = iso.split('-').map(Number)
  return Date.UTC(y, m - 1, d)
}

export const todayIso = () => {
  const now = new Date()
  return new Date(Date.UTC(now.getFullYear(), now.getMonth(), now.getDate())).toISOString().slice(0, 10)
}

export const daysBetween = (fromIso: string, toIso: string) => Math.round((parseIso(toIso) - parseIso(fromIso)) / DAY)

const fmt = (iso: string, opts: Intl.DateTimeFormatOptions) =>
  new Intl.DateTimeFormat('en-US', { timeZone: 'UTC', ...opts }).format(parseIso(iso))

/** "Aug 10, 2026" */
export const formatDate = (iso: string) => fmt(iso, { month: 'short', day: 'numeric', year: 'numeric' })

/** "Nov 30" */
export const formatDay = (iso: string) => fmt(iso, { month: 'short', day: 'numeric' })

/** "Aug 10 – Dec 18, 2026", or "Aug 10, 2026 – Jul 23, 2027" across years. */
export const formatRange = (startIso: string, endIso: string) =>
  startIso.slice(0, 4) === endIso.slice(0, 4)
    ? `${formatDay(startIso)} – ${formatDate(endIso)}`
    : `${formatDate(startIso)} – ${formatDate(endIso)}`

/** "Aug 10 – Nov 30" (no year). */
export const formatDayRange = (startIso: string, endIso: string) => `${formatDay(startIso)} – ${formatDay(endIso)}`

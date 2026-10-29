// Shared display formats for API values. Dates come in two shapes: DateOnly ("2026-10-26", shown as
// that calendar day) and DateTimeOffset ("2026-10-26T03:12:00+00:00", shown in local time).

const isDateOnly = (value: string) => /^\d{4}-\d{2}-\d{2}$/.test(value)
const toDate = (value: string) => (isDateOnly(value) ? new Date(`${value}T00:00:00Z`) : new Date(value))

function format(value: string | null | undefined, options: Intl.DateTimeFormatOptions, fallback = '—') {
  if (!value) return fallback
  const date = toDate(value)
  if (Number.isNaN(date.getTime())) return fallback
  return new Intl.DateTimeFormat('en-US', isDateOnly(value) ? { ...options, timeZone: 'UTC' } : options).format(date)
}

/** "Oct 26" */
export const shortDate = (value: string | null | undefined) => format(value, { month: 'short', day: 'numeric' })

/** "Oct 26, 2026" */
export const longDate = (value: string | null | undefined) => format(value, { month: 'short', day: 'numeric', year: 'numeric' })

/** "Oct 26, 2026, 3:04 PM" */
export const dateTime = (value: string | null | undefined) =>
  format(value, { month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit' })

/** "3:04 PM" */
export const timeOfDay = (value: string | null | undefined) => format(value, { hour: 'numeric', minute: '2-digit' })

/** Today as a DateOnly string in the user's time zone, e.g. "2026-10-27". */
export function todayIso() {
  const now = new Date()
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`
}

/** Whole days from `from` to `to` (DateOnly or date-time; `to` defaults to now). */
export function daysBetween(from: string, to?: string) {
  const start = toDate(from).getTime()
  const end = to ? toDate(to).getTime() : Date.now()
  return Math.floor((end - start) / 86_400_000)
}

/** "just now", "5 min ago", "3 h ago", "2 d ago", then the date. */
export function timeAgo(value: string | null | undefined) {
  if (!value) return '—'
  const seconds = (Date.now() - toDate(value).getTime()) / 1000
  if (seconds < 60) return 'just now'
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`
  if (seconds < 86_400) return `${Math.floor(seconds / 3600)} h ago`
  if (seconds < 7 * 86_400) return `${Math.floor(seconds / 86_400)} d ago`
  return shortDate(value)
}

/** "840 KB", "1.2 MB" */
export function formatBytes(bytes: number | null | undefined) {
  if (bytes === null || bytes === undefined) return '—'
  if (bytes < 1024) return `${bytes} B`
  const units = ['KB', 'MB', 'GB']
  let value = bytes / 1024
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value >= 10 ? Math.round(value) : value.toFixed(1)} ${units[unit]}`
}

/** "1st Sem 2026–27" (the year name without its "AY " prefix). */
export const termShortLabel = (term: { shortName: string; academicYearName: string } | null | undefined) =>
  term ? `${term.shortName} ${term.academicYearName.replace(/^AY\s*/, '')}` : '—'

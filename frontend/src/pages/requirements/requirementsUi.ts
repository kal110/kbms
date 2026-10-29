import { ApiError } from '../../api/client'
import type {
  AcademicTermDto,
  AcademicYearDto,
  FileLimit,
  RequirementItemDto,
  RequirementItemInput,
  RequirementRule,
  RequirementSetDto,
  UpdateRequirementSetRequest,
} from '../../api/types'
import { shortDate } from '../../lib/format'

// Display helpers shared by the program checklist (frame 18) and the college / department lists
// (frame 23). Slots come from the API as RequirementItemDto and go back as RequirementItemInput.

export const ruleOptions: { value: RequirementRule; label: string }[] = [
  { value: 'Required', label: 'Required' },
  { value: 'Optional', label: 'Optional' },
]

const unit = (n: number) => (n === 1 ? 'PDF' : 'PDFs')

/** "Exactly 1 PDF", "1–3 PDFs", "Up to 2 PDFs" */
export function filesLabel(rule: RequirementRule, files: FileLimit) {
  if (rule === 'Optional') return `Up to ${files.max} ${unit(files.max)}`
  if (files.min === files.max) return `Exactly ${files.max} ${unit(files.max)}`
  return `${files.min}–${files.max} PDFs`
}

export const lowerFirst = (s: string) => s.charAt(0).toLowerCase() + s.slice(1)

/** The API's key for a label (Checklists.KeyFromLabel): "Course outcomes matrix" → "course_outcomes_matrix". */
export function keyFromLabel(label: string) {
  let key = label
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '')
  if (/^[0-9]/.test(key)) key = `k_${key}`
  return key.length > 60 ? key.slice(0, 60).replace(/_+$/, '') : key
}

/** `key`, or `key_2`, `key_3`… when another slot already uses it. */
export function uniqueKey(key: string, taken: ReadonlySet<string>) {
  if (!taken.has(key)) return key
  let n = 2
  while (taken.has(`${key}_${n}`)) n++
  return `${key}_${n}`
}

/** A required slot takes at least one file; an optional one may take none. */
export const filesFor = (rule: RequirementRule, files: FileLimit): FileLimit => ({
  min: rule === 'Required' ? Math.min(Math.max(1, files.min), files.max) : 0,
  max: files.max,
})

export const toInput = (item: RequirementItemDto): RequirementItemInput => ({
  key: item.key,
  label: item.label,
  documentTypeId: item.documentTypeId,
  rule: item.rule,
  files: item.files,
  due: item.due,
})

/** A draft as the PUT body that would leave it unchanged. */
export const requestOf = (draft: RequirementSetDto): UpdateRequirementSetRequest => ({
  notes: draft.notes,
  effectiveFromTermId: draft.effectiveFrom?.id ?? null,
  items: draft.items.map(toInput),
})

export interface SetChange {
  kind: 'added' | 'removed' | 'changed'
  text: string
}

/** Human-readable changes between the published slots and a draft. */
export function diffRequirements(before: RequirementItemDto[], after: RequirementItemDto[]): SetChange[] {
  const changes: SetChange[] = []
  for (const item of after) {
    const old = before.find((b) => b.key === item.key)
    if (!old) {
      changes.push({
        kind: 'added',
        text: `${item.label} · ${item.documentTypeCode} · ${lowerFirst(item.rule)}, ${lowerFirst(filesLabel(item.rule, item.files))}`,
      })
      continue
    }
    const parts: string[] = []
    if (old.label !== item.label) parts.push(`renamed from “${old.label}”`)
    if (old.documentTypeId !== item.documentTypeId) parts.push(`type ${old.documentTypeCode} → ${item.documentTypeCode}`)
    if (old.rule !== item.rule) parts.push(`${lowerFirst(old.rule)} → ${lowerFirst(item.rule)}`)
    const oldFiles = lowerFirst(filesLabel(old.rule, old.files))
    const newFiles = lowerFirst(filesLabel(item.rule, item.files))
    if (oldFiles !== newFiles) parts.push(`${oldFiles.replace(/ PDFs?$/, '')} → ${newFiles}`)
    if ((old.due ?? null) !== (item.due ?? null)) {
      parts.push(`due ${old.due ? shortDate(old.due) : 'not set'} → ${item.due ? shortDate(item.due) : 'not set'}`)
    }
    if (parts.length) changes.push({ kind: 'changed', text: `${item.label} · ${parts.join(' · ')}` })
  }
  for (const old of before) {
    if (!after.some((a) => a.key === old.key)) changes.push({ kind: 'removed', text: `${old.label} · ${old.documentTypeCode} · removed` })
  }
  return changes
}

/** Field errors the API returned for slot `index` of the list it was sent ("items[2].label" → label). */
export function slotFieldErrors(error: unknown, index: number): Record<string, string> {
  if (!(error instanceof ApiError) || !error.errors) return {}
  const prefix = `items[${index}].`
  const result: Record<string, string> = {}
  for (const [path, messages] of Object.entries(error.errors)) {
    if (path === `items[${index}]`) result.slot = messages.join(' ')
    else if (path.startsWith(prefix)) result[path.slice(prefix.length)] = messages.join(' ')
  }
  return result
}

/** Slot errors by key, given the keys of the list that was sent (in order). */
export function errorsByKey(error: unknown, keys: readonly string[]) {
  const byKey: Record<string, string> = {}
  if (!(error instanceof ApiError)) return byKey
  if (error.errors) {
    for (const [path, messages] of Object.entries(error.errors)) {
      const match = /^items\[(\d+)\]/.exec(path)
      const key = match ? keys[Number(match[1])] : undefined
      if (key) byKey[key] = [byKey[key], ...messages].filter(Boolean).join(' ')
    }
  }
  // Publish refuses slots of retired types and names them (`keys` extension). Other 422s that name
  // slots (duplicate-type) carry their message in `errors` above.
  const retired = error.code === 'type-retired' ? error.problem?.keys : undefined
  if (Array.isArray(retired)) {
    for (const key of retired) if (typeof key === 'string') byKey[key] = 'This document type is retired; pick its replacement.'
  }
  return byKey
}

/** Terms a draft can take effect from (active or planned), earliest first. */
export const openTerms = (years: readonly AcademicYearDto[]): AcademicTermDto[] =>
  years
    .flatMap((y) => y.terms)
    .filter((t) => t.status !== 'Closed')
    .sort((a, b) => a.startsOn.localeCompare(b.startsOn))

export const findTerm = (years: readonly AcademicYearDto[], id: string | null | undefined) =>
  id ? years.flatMap((y) => y.terms).find((t) => t.id === id) : undefined

/** "1st Sem AY 2026–27" for a term reference. */
export const termName = (term: { shortName: string; academicYearName: string } | null | undefined) =>
  term ? `${term.shortName} ${term.academicYearName}`.trim() : 'no term set'

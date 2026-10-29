import type { SearchMode } from '../../api/library'
import type { SearchHit } from '../../api/types'

// The library's two search modes (backend SearchService). Smart finds documents by their title,
// number and description, and by meaning once the AI index is on; Exact finds the words as typed
// anywhere in the text. A search in "double quotes" is exact whichever mode is picked.

const QUOTED = /^["“”](.*)["“”]$/s

/** The mode the server runs: a quoted search is exact. */
export const effectiveMode = (query: string, mode: SearchMode): SearchMode => (QUOTED.test(query.trim()) ? 'exact' : mode)

/** The words an exact search looks for: quotes dropped, spacing collapsed. */
export const phraseOf = (query: string) => (QUOTED.exec(query.trim())?.[1] ?? query).trim().replace(/\s+/g, ' ')

export const searchPlaceholder = (mode: SearchMode, smart: string) =>
  mode === 'exact' ? 'Exact words, in the text too…' : smart

/** Where a hit opens: at the matching page when there is one. */
export const hitLink = (documentId: string, hit: SearchHit | undefined) =>
  hit?.page && hit.match !== 'Details' ? `/documents/${documentId}#page=${hit.page}` : `/documents/${documentId}`

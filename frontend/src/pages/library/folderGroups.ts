import type { DocumentGroup } from '../../api/types'

// "By year & type" on Academic folders: a folder's documents as year folders, each with a folder per type.

export const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`

/** A year folder: the type folders of one academic year ("none" for untagged types). */
export interface GroupYear {
  key: string
  id: string | null
  name: string
  documents: number
  groups: DocumentGroup[]
}

/** The API sorts by year (newest first, untagged last), so a year's types come together. */
export function groupYearsOf(groups: DocumentGroup[]): GroupYear[] {
  const years: GroupYear[] = []
  for (const g of groups) {
    const last = years.at(-1)
    if (last && last.id === g.academicYearId) {
      last.groups.push(g)
      last.documents += g.documents
    } else {
      years.push({
        key: g.academicYearId ?? 'none',
        id: g.academicYearId,
        name: g.academicYearName ?? 'No academic year',
        documents: g.documents,
        groups: [g],
      })
    }
  }
  return years
}

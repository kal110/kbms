import type { DocumentState, VersionProcessingStatus } from '../../api/types'
import type { BadgeTone } from '../../components/Badge'

/** The current version's processing state as a badge. Ready = text extracted and keyword-searchable. */
export function indexBadge(status: VersionProcessingStatus | null | undefined): { label: string; tone: BadgeTone } {
  switch (status) {
    case 'Ready':
      return { label: 'Searchable', tone: 'success' }
    case 'Pending':
      return { label: 'Queued', tone: 'neutral' }
    case 'Processing':
      return { label: 'Extracting text', tone: 'info' }
    case 'Failed':
      return { label: 'Index failed · retry', tone: 'danger' }
    default:
      return { label: 'No file yet', tone: 'neutral' }
  }
}

export const isProcessing = (status: VersionProcessingStatus | null | undefined) => status === 'Pending' || status === 'Processing'

/** A document's status chip (the design calls it the lifecycle). */
export const stateLabel: Record<DocumentState, string> = {
  Published: 'Published',
  Draft: 'Draft',
  InReview: 'In review',
  Archived: 'Archived',
}

export const stateTone: Record<DocumentState, BadgeTone> = {
  Published: 'success',
  Draft: 'neutral',
  InReview: 'warning',
  Archived: 'neutral',
}

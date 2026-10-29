import { cached } from './cache'
import { api } from './client'
import type { AcademicTermDto, AcademicYearDto, DocumentTypeDto, SubmissionTypeDto } from './types'

// Reference lists every signed-in user reads (pickers, term labels). Cached per account;
// Settings screens call invalidate('document-types') etc. after editing.

export const getDocumentTypes = (includeInactive = false) =>
  cached(`document-types:${includeInactive}`, () =>
    api.get<DocumentTypeDto[]>('/document-types', { query: { includeInactive } }),
  )

export const getSubmissionTypes = () => cached('submission-types', () => api.get<SubmissionTypeDto[]>('/submission-types'))

/** Academic years with their terms, newest first. */
export const getAcademicYears = () => cached('academic-years', () => api.get<AcademicYearDto[]>('/academic-years'))

export const getActiveTerm = () => cached('active-term', () => api.get<AcademicTermDto>('/academic-terms/active'))

/** "1st Sem AY 2026–27" */
export const termLabel = (term: Pick<AcademicTermDto, 'shortName' | 'academicYearName'>) =>
  `${term.shortName} ${term.academicYearName}`.trim()

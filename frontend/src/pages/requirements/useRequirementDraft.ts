import { useState } from 'react'
import type { Dispatch, SetStateAction } from 'react'
import { ApiError } from '../../api/client'
import {
  createRequirementDraft,
  deleteRequirementDraft,
  getRequirementSet,
  publishRequirementSet,
  updateRequirementDraft,
} from '../../api/requirements'
import type {
  RequirementScope,
  RequirementSetDto,
  ScopeRequirementSetsDto,
  ScopeSetGroupDto,
  UpdateRequirementSetRequest,
} from '../../api/types'
import { useAction } from '../../api/useApi'
import { keyFromLabel, requestOf } from './requirementsUi'

type SetsData = ScopeRequirementSetsDto | undefined

/** Puts a saved set into its submission type's group, adding the group for a type that had no list. */
function patchGroup(data: SetsData, set: RequirementSetDto, patch: Partial<Pick<ScopeSetGroupDto, 'published' | 'draft'>>): SetsData {
  if (!data) return data
  const typeId = set.submissionType.id
  const sets = data.sets.some((g) => g.submissionType.id === typeId)
    ? data.sets.map((g) => (g.submissionType.id === typeId ? { ...g, ...patch } : g))
    : [...data.sets, { submissionType: set.submissionType, published: null, draft: null, inherited: [], ...patch }]
  return { ...data, sets }
}

/**
 * Draft → edit → publish for one scope's lists. Every edit is saved to the server draft (with its
 * ETag); the first edit starts the draft from the published list, or from `copyFromSetId`.
 */
export function useRequirementDraft(
  scope: RequirementScope,
  scopeId: string,
  setData: Dispatch<SetStateAction<SetsData>>,
  reload: () => void,
) {
  const action = useAction((op: () => Promise<RequirementSetDto | null>) => op())
  // Keys of the slots last sent, so field errors ("items[2].label") can be shown on their row.
  const [sentKeys, setSentKeys] = useState<string[]>([])

  const showDraft = (draft: RequirementSetDto) => setData((d) => patchGroup(d, draft, { draft }))

  const startDraft = async (submissionTypeId: string, copyFromSetId: string | null) => {
    try {
      return await createRequirementDraft(scope, scopeId, { submissionTypeId, copyFromSetId })
    } catch (error) {
      // Someone (another tab) started one meanwhile: carry on with theirs.
      const draftId = error instanceof ApiError && error.code === 'draft-exists' ? error.problem?.draftId : undefined
      if (typeof draftId === 'string') return getRequirementSet(draftId)
      throw error
    }
  }

  const run = (op: () => Promise<RequirementSetDto | null>) =>
    action.run(async () => {
      try {
        return await op()
      } catch (error) {
        // The list changed elsewhere (stale ETag, already published): show the current lists.
        if (error instanceof ApiError && (error.isStale || error.status === 409)) {
          reload()
          if (error.isStale) throw new Error('Someone else changed this list since you loaded it. It now shows their version; make your change again.')
        }
        throw error
      }
    })

  const edit = (
    group: ScopeSetGroupDto | undefined,
    submissionTypeId: string,
    change: (current: UpdateRequirementSetRequest) => UpdateRequirementSetRequest,
    copyFromSetId: string | null = null,
  ) =>
    run(async () => {
      let draft = group?.draft ?? (await startDraft(submissionTypeId, copyFromSetId))
      if (draft !== group?.draft) showDraft(draft)
      const current = requestOf(draft)
      const next = change(current)
      setSentKeys(next.items.map((i) => i.key ?? keyFromLabel(i.label)))
      if (JSON.stringify(next) === JSON.stringify(current)) return draft
      draft = await updateRequirementDraft(draft.id, draft.etag, next)
      showDraft(draft)
      return draft
    })

  const publish = (draft: RequirementSetDto) =>
    run(async () => {
      setSentKeys(draft.items.map((i) => i.key))
      const published = await publishRequirementSet(draft.id, draft.etag)
      setData((d) => patchGroup(d, published, { published, draft: null }))
      return published
    })

  const discard = (draft: RequirementSetDto) =>
    run(async () => {
      await deleteRequirementDraft(draft.id, draft.etag)
      setData((d) => patchGroup(d, draft, { draft: null }))
      return null
    })

  return { edit, publish, discard, sentKeys, pending: action.pending, error: action.error, clearError: action.clearError }
}

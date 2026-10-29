import { useState } from 'react'
import type { AcademicTermDto, DocumentTypeDto, RequirementItemDto, RequirementItemInput, RequirementRule } from '../../api/types'
import { termLabel } from '../../api/reference'
import { InlineError } from '../../components/AsyncState'
import { Button } from '../../components/Button'
import { TagChip } from '../../components/Chips'
import { Modal } from '../../components/Modal'
import { Segmented } from '../../components/Segmented'
import { filesFor, filesLabel, keyFromLabel, ruleOptions } from './requirementsUi'
import styles from './RequiredDocumentsPage.module.css'

interface RequirementModalProps {
  /** Undefined when adding a new requirement. */
  initial?: RequirementItemDto
  /** The list's own slots. */
  items: RequirementItemDto[]
  /** Slots required above this list (college, department): their types can't be added again. */
  inherited: RequirementItemDto[]
  documentTypes: DocumentTypeDto[]
  activeTerm?: AcademicTermDto
  /** Saves into the draft; resolves true when the API took it. */
  onSave: (requirement: RequirementItemInput) => Promise<boolean>
  pending: boolean
  error: unknown
  /** The API's errors for this slot, by field (label, key, documentTypeId, files, due). */
  fieldErrors: Record<string, string>
  onClose: () => void
}

const counts = [1, 2, 3, 4, 5]

export function RequirementModal({
  initial,
  items,
  inherited,
  documentTypes,
  activeTerm,
  onSave,
  pending,
  error,
  fieldErrors,
  onClose,
}: RequirementModalProps) {
  const [typeId, setTypeId] = useState(initial?.documentTypeId ?? '')
  const [label, setLabel] = useState(initial?.label ?? '')
  const [labelTouched, setLabelTouched] = useState(Boolean(initial))
  const [rule, setRule] = useState<RequirementRule>(initial?.rule ?? 'Required')
  const [min, setMin] = useState(Math.max(1, initial?.files.min ?? 1))
  const [max, setMax] = useState(initial?.files.max ?? 1)
  const [due, setDue] = useState(initial?.due ?? '')
  const [tried, setTried] = useState(false)

  const key = initial?.key ?? keyFromLabel(label)
  const keyTaken = !initial && items.some((i) => i.key === key)
  const type = documentTypes.find((t) => t.id === typeId)
  const files = filesFor(rule, { min, max })
  const maxCounts = counts.includes(max) ? counts : [...counts, max]

  const blockedReason = (id: string) => {
    if (id === initial?.documentTypeId) return ''
    const source = inherited.find((i) => i.documentTypeId === id)
    if (source) return `required by ${source.source?.code ?? 'a broader list'}`
    if (items.some((i) => i.documentTypeId === id)) return 'already in this list'
    return ''
  }

  const example = (() => {
    if (!type) return ''
    if (type.tag === 'Term') return activeTerm ? termLabel(activeTerm) : 'the submission’s term'
    if (type.tag === 'Year') return activeTerm?.academicYearName ?? 'the submission’s year'
    return 'no period tag'
  })()

  const save = async () => {
    setTried(true)
    if (!typeId || !label.trim() || !key || keyTaken || Boolean(blockedReason(typeId))) return
    await onSave({ key, label: label.trim(), documentTypeId: typeId, rule, files, due: due || null })
  }

  const hasFieldErrors = Object.keys(fieldErrors).length > 0

  return (
    <Modal
      title={initial ? `Edit requirement · ${initial.label}` : 'Add requirement'}
      description="Pick a document type first. Files attached to this slot get that type and the submission's year and term."
      width={560}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" disabled={pending} onClick={save}>
            {pending ? 'Saving…' : initial ? 'Save changes' : 'Add requirement'}
          </Button>
        </>
      }
    >
      <label className={styles.formField}>
        <span className={styles.formLabel}>
          Document type <span className={styles.requiredMark}>*</span>
        </span>
        <select
          className="field"
          value={typeId}
          onChange={(e) => {
            setTypeId(e.target.value)
            const picked = documentTypes.find((t) => t.id === e.target.value)
            if (!labelTouched && picked) setLabel(picked.name)
          }}
        >
          <option value="">Choose a document type…</option>
          {documentTypes
            .filter((t) => (t.active && t.scope !== 'NonAcademic') || t.id === initial?.documentTypeId)
            .map((t) => {
              const reason = blockedReason(t.id)
              return (
                <option key={t.id} value={t.id} disabled={Boolean(reason)}>
                  {t.name} · {t.code}
                  {reason ? ` — ${reason}` : ''}
                  {t.active ? '' : ' — retired'}
                </option>
              )
            })}
        </select>
        {type ? (
          <span className={styles.formHelpRow}>
            <TagChip tag={type.tag} label={type.tag === 'None' ? 'None' : type.tag} />
            Files get this tag automatically, e.g. {example}.
          </span>
        ) : (
          tried && <span className={styles.formError}>Choose a document type.</span>
        )}
        {fieldErrors.documentTypeId && <span className={styles.formError}>{fieldErrors.documentTypeId}</span>}
      </label>

      <label className={styles.formField}>
        <span className={styles.formLabel}>
          Label <span className={styles.requiredMark}>*</span>
        </span>
        <input
          className="field"
          value={label}
          placeholder="Defaults to the type name"
          onChange={(e) => {
            setLabel(e.target.value)
            setLabelTouched(true)
          }}
        />
        <span className={styles.formHelp}>
          key: {key || '—'}
          {initial ? ' (fixed once published)' : ''}
        </span>
        {tried && !label.trim() && <span className={styles.formError}>Give the requirement a label.</span>}
        {keyTaken && <span className={styles.formError}>Another requirement already uses the key “{key}”.</span>}
        {fieldErrors.label && <span className={styles.formError}>{fieldErrors.label}</span>}
        {fieldErrors.key && <span className={styles.formError}>{fieldErrors.key}</span>}
      </label>

      <div className={styles.formRow}>
        <div className={styles.formField}>
          <span className={styles.formLabel}>Rule</span>
          <Segmented<RequirementRule> ariaLabel="Rule" size="sm" options={ruleOptions} value={rule} onChange={setRule} />
        </div>
        {rule === 'Required' && (
          <label className={styles.formField}>
            <span className={styles.formLabel}>At least</span>
            <select className="field" value={Math.min(min, max)} onChange={(e) => setMin(Number(e.target.value))}>
              {maxCounts
                .filter((n) => n <= max)
                .map((n) => (
                  <option key={n} value={n}>
                    {n} PDF{n > 1 ? 's' : ''}
                  </option>
                ))}
            </select>
          </label>
        )}
        <label className={styles.formField}>
          <span className={styles.formLabel}>At most</span>
          <select className="field" value={max} onChange={(e) => setMax(Number(e.target.value))}>
            {maxCounts.map((n) => (
              <option key={n} value={n}>
                {n} PDF{n > 1 ? 's' : ''}
              </option>
            ))}
          </select>
        </label>
      </div>
      <span className={styles.formHelp}>Faculty see: {filesLabel(rule, files)}</span>
      {fieldErrors.files && <span className={styles.formError}>{fieldErrors.files}</span>}

      <label className={styles.formField}>
        <span className={styles.formLabel}>Due date</span>
        <span className={styles.formHelpRow}>
          <input className="field" type="date" value={due} onChange={(e) => setDue(e.target.value)} />
          {due && (
            <button type="button" className={styles.linkButton} onClick={() => setDue('')}>
              Clear
            </button>
          )}
        </span>
        <span className={styles.formHelp}>Optional. Shown on the faculty checklist and used for overdue reminders.</span>
        {fieldErrors.due && <span className={styles.formError}>{fieldErrors.due}</span>}
      </label>

      <InlineError error={fieldErrors.slot ? new Error(fieldErrors.slot) : hasFieldErrors ? undefined : error} />
    </Modal>
  )
}

import { useEffect, useRef, useState } from 'react'
import type { DragEvent } from 'react'
import { FileUp, FolderOpen, Lock } from 'lucide-react'
import type { AcademicTag, ChecklistItem, SubmissionDetail } from '../../api/types'
import { uploadFile } from '../../api/uploads'
import { useAction } from '../../api/useApi'
import { addAttachment, removeAttachment } from '../../api/workflow'
import { InlineError } from '../../components/AsyncState'
import { Button } from '../../components/Button'
import { TagChip, TypeChip } from '../../components/Chips'
import { Modal } from '../../components/Modal'
import { shortDate } from '../../lib/format'
import { filedPath, filesLabel, lowerFirst, periodTagText, ruleOf } from './submissionUi'
import styles from './MySubmissionsPage.module.css'

interface AttachModalProps {
  submission: SubmissionDetail
  slot: ChecklistItem
  /** The slot's document type period tag, when known. */
  tag?: AcademicTag
  /** The submitter's own folder name (from /folders/me), when known. */
  folderName?: string
  onClose: () => void
  /** A file was attached: the updated submission. */
  onAttached: (next: SubmissionDetail) => void
  /** A file was removed (the modal stays open): the updated submission. */
  onChanged: (next: SubmissionDetail) => void
}

const isPdf = (file: File) => file.type === 'application/pdf' || file.name.toLowerCase().endsWith('.pdf')

const sizeLabel = (bytes: number) =>
  bytes > 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`

// "new" adds another file to a multi-file slot; otherwise the id of the document to add a version to.
const NEW_FILE = 'new'

export function AttachModal({ submission, slot, tag, folderName, onClose, onAttached, onChanged }: AttachModalProps) {
  const [file, setFile] = useState<File | null>(null)
  const [pickError, setPickError] = useState('')
  const [dragging, setDragging] = useState(false)
  const [progress, setProgress] = useState<number | null>(null)
  const files = slot.attachments
  const hasRoom = files.length < slot.maxFiles
  const [target, setTarget] = useState(hasRoom ? NEW_FILE : (files[0]?.documentId ?? NEW_FILE))
  const controller = useRef<AbortController | null>(null)

  // Closing the modal mid-upload cancels the transfer.
  useEffect(() => () => controller.current?.abort(), [])

  // A single-file slot that already has its file takes the upload as that file's next version.
  const revising = slot.maxFiles === 1 ? files[0] : files.find((a) => a.documentId === target)
  const nextVersion = (revising?.versionNumber ?? 0) + 1
  const canRemove = submission.allowedActions.includes('removeAttachment')

  const attach = useAction(async (picked: File) => {
    controller.current = new AbortController()
    setProgress(0)
    try {
      const uploadId = await uploadFile(picked, {
        documentId: revising?.documentId,
        signal: controller.current.signal,
        onProgress: (p) => setProgress(p.total ? Math.round((p.loaded / p.total) * 100) : null),
      })
      return await addAttachment(submission.id, submission.version, {
        requirementKey: slot.key,
        uploadSessionId: uploadId,
        documentId: revising?.documentId ?? null,
      })
    } finally {
      setProgress(null)
    }
  })
  const remove = useAction((attachmentId: string) => removeAttachment(submission.id, submission.version, attachmentId))

  const pick = (picked?: File | null) => {
    if (!picked) return
    if (!isPdf(picked)) {
      setPickError('Only PDF files can be attached to this slot.')
      setFile(null)
      return
    }
    setPickError('')
    attach.clearError()
    setFile(picked)
  }

  const onDrop = (e: DragEvent<HTMLLabelElement>) => {
    e.preventDefault()
    setDragging(false)
    pick(e.dataTransfer.files[0])
  }

  const save = async () => {
    if (!file) return
    const next = await attach.run(file)
    if (next) onAttached(next)
  }

  let versionNote = `This is v1 of ${lowerFirst(slot.label)} in this submission.`
  if (revising && submission.status === 'RevisionRequested') {
    versionNote = `This becomes v${nextVersion}. Your reviewer keeps seeing v${revising.pinnedVersionNumber ?? revising.versionNumber} until you resubmit.`
  } else if (revising) {
    versionNote = `This becomes v${nextVersion} and replaces v${revising.versionNumber} in the draft.`
  } else if (files.length) {
    versionNote = `This adds file ${files.length + 1} of up to ${slot.maxFiles} to ${lowerFirst(slot.label)}.`
  }

  const uploading = attach.pending
  const busyLabel = progress !== null && progress < 100 ? `Uploading… ${progress}%` : 'Attaching…'

  return (
    <Modal
      title={revising ? `Upload a new version · ${slot.label}` : `Attach · ${slot.label}`}
      description={`${submission.number} · ${submission.title}`}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" disabled={!file || uploading} onClick={save}>
            {uploading ? busyLabel : revising ? `Upload v${nextVersion}` : 'Attach file'}
          </Button>
        </>
      }
    >
      {files.length > 0 && slot.maxFiles > 1 && (
        <div className={styles.formField}>
          <span className={styles.formLabel}>In this slot</span>
          <div className={styles.previewList}>
            {files.map((a) => (
              <div key={a.id} className={styles.previewItem}>
                <span className={styles.slotText}>
                  <span className={styles.slotTitle}>{a.fileName}</span>
                  <span className={styles.slotNote}>
                    v{a.versionNumber} · uploaded {shortDate(a.uploadedAt)}
                  </span>
                </span>
                {canRemove && (
                  <Button size="sm" variant="ghost" disabled={remove.pending || uploading} onClick={async () => {
                    const next = await remove.run(a.id)
                    if (next) onChanged(next)
                  }}>
                    Remove
                  </Button>
                )}
              </div>
            ))}
          </div>
          <InlineError error={remove.error} />
        </div>
      )}

      {files.length > 0 && slot.maxFiles > 1 && (
        <label className={styles.formField}>
          <span className={styles.formLabel}>Upload as</span>
          <select className="field" value={target} onChange={(e) => setTarget(e.target.value)} disabled={uploading}>
            {hasRoom && <option value={NEW_FILE}>Another file ({files.length + 1} of up to {slot.maxFiles})</option>}
            {files.map((a) => (
              <option key={a.documentId} value={a.documentId}>
                New version of {a.fileName} (v{a.versionNumber + 1})
              </option>
            ))}
          </select>
        </label>
      )}

      <div className={styles.formField}>
        <span className={styles.formLabel}>
          File <span className={styles.requiredMark}>*</span>
        </span>
        <label
          className={`${styles.dropzone} ${dragging ? styles.dropzoneActive : ''}`}
          onDragOver={(e) => {
            e.preventDefault()
            setDragging(true)
          }}
          onDragLeave={() => setDragging(false)}
          onDrop={onDrop}
        >
          <input
            type="file"
            accept=".pdf,application/pdf"
            className="visually-hidden"
            disabled={uploading}
            onChange={(e) => pick(e.target.files?.[0])}
          />
          <FileUp size={20} aria-hidden />
          {file ? (
            <span>
              <strong>{file.name}</strong> · {sizeLabel(file.size)}
            </span>
          ) : (
            <span>
              <strong>Choose a PDF</strong> or drop it here ·{' '}
              {lowerFirst(filesLabel(ruleOf(slot), slot.minFiles, slot.maxFiles))} for this slot
            </span>
          )}
        </label>
        {pickError && <span className={styles.formError}>{pickError}</span>}
        <span className={styles.formHelp}>{versionNote}</span>
        <InlineError error={attach.error} />
      </div>

      {slot.documentType && (
        <div className={styles.formField}>
          <span className={styles.formLabel}>
            Document type <span className={styles.requiredMark}>*</span>
          </span>
          <div className={styles.lockedField}>
            <TypeChip label={`${slot.documentType.name} · ${slot.documentType.code}`} />
            <Lock size={13} aria-hidden className={styles.lockIcon} />
            <span className={styles.formHelp}>Set by the requirement slot. You never choose a type.</span>
          </div>
        </div>
      )}

      {tag && (
        <div className={styles.formField}>
          <span className={styles.formLabel}>Period tag</span>
          <div className={styles.lockedField}>
            <TagChip tag={tag} label={tag === 'None' ? 'None' : tag} />
            <span className={styles.lockedValue}>{periodTagText(tag, submission.term)}</span>
            <span className={styles.formHelp}>Filled in from the submission</span>
          </div>
        </div>
      )}

      {tag && folderName && (
        <div className={styles.formField}>
          <span className={styles.formLabel}>Filed to</span>
          <div className={styles.lockedField}>
            <FolderOpen size={15} aria-hidden className={styles.lockIcon} />
            <span className={styles.lockedValue}>{filedPath(submission.unit.code, folderName, submission.term, tag)}</span>
          </div>
        </div>
      )}
    </Modal>
  )
}

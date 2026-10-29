import { useRef, useState } from 'react'
import { FileText, Upload, X } from 'lucide-react'
import { getAcademicYears, getActiveTerm, getDocumentTypes } from '../../api/reference'
import { useApi } from '../../api/useApi'
import { ErrorState } from '../../components/AsyncState'
import { Button } from '../../components/Button'
import { TagChip } from '../../components/Chips'
import { Modal } from '../../components/Modal'
import { formatBytes } from '../../lib/format'
import styles from './UploadModal.module.css'

export interface UploadRequest {
  files: File[]
  typeId: string
  yearId?: string
  termId?: string
}

interface UploadModalProps {
  folderName: string
  onClose: () => void
  onSubmit: (request: UploadRequest) => void
}

/** The API's limit per file (UploadService.MaxBytes). */
const MAX_BYTES = 100 * 1024 * 1024

const isPdf = (f: File) => f.type === 'application/pdf' || f.name.toLowerCase().endsWith('.pdf')

/** "Upload PDFs": every file needs a document type, plus the year or term that type requires (422 type_required / tag_required). */
export function UploadModal({ folderName, onClose, onSubmit }: UploadModalProps) {
  const inputRef = useRef<HTMLInputElement>(null)
  const reference = useApi(
    () => Promise.all([getDocumentTypes(), getAcademicYears(), getActiveTerm().catch(() => null)]),
    [],
  )
  const [types, years, activeTerm] = reference.data ?? [[], [], null]
  const [files, setFiles] = useState<File[]>([])
  const [rejected, setRejected] = useState<string[]>([])
  const [typeId, setTypeId] = useState('')
  const [yearChoice, setYearChoice] = useState<string>()
  const [termChoice, setTermChoice] = useState<string>()
  const [submitted, setSubmitted] = useState(false)
  const [dragging, setDragging] = useState(false)

  const terms = years.flatMap((y) => y.terms)
  const yearId = yearChoice ?? activeTerm?.academicYearId ?? years[0]?.id ?? ''
  const termId = termChoice ?? activeTerm?.id ?? terms[0]?.id ?? ''
  const type = types.find((t) => t.id === typeId)
  const errors = {
    files: files.length === 0 ? 'Add at least one PDF.' : '',
    type: !type ? 'Choose a document type. Every document needs one.' : '',
  }
  const valid = !errors.files && !errors.type

  const addFiles = (list: FileList | null) => {
    if (!list) return
    const picked = Array.from(list)
    const ok = picked.filter((f) => isPdf(f) && f.size > 0 && f.size <= MAX_BYTES)
    setRejected(
      picked
        .filter((f) => !ok.includes(f))
        .map((f) => (isPdf(f) ? `${f.name} is ${f.size === 0 ? 'empty' : 'larger than 100 MB'}.` : `${f.name} isn't a PDF.`)),
    )
    setFiles((prev) => [...prev, ...ok.filter((p) => !prev.some((x) => x.name === p.name))])
  }

  const submit = () => {
    setSubmitted(true)
    if (!valid || !type) return
    onSubmit({
      files,
      typeId,
      yearId: type.tag === 'Year' ? yearId : type.tag === 'Term' ? terms.find((t) => t.id === termId)?.academicYearId : undefined,
      termId: type.tag === 'Term' ? termId : undefined,
    })
  }

  return (
    <Modal
      title="Upload PDFs"
      description={`Into ${folderName}. Each file becomes a document; text is extracted after the upload so it can be searched.`}
      width={560}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" icon={Upload} onClick={submit} disabled={!reference.data || (submitted && !valid)}>
            Start upload{files.length > 1 ? ` (${files.length})` : ''}
          </Button>
        </>
      }
    >
      <div
        className={`${styles.drop} ${dragging ? styles.dragging : ''}`}
        onDragOver={(e) => {
          e.preventDefault()
          setDragging(true)
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={(e) => {
          e.preventDefault()
          setDragging(false)
          addFiles(e.dataTransfer.files)
        }}
      >
        <Upload size={20} className={styles.dropIcon} aria-hidden />
        <span>
          Drop PDFs here or{' '}
          <button type="button" className={styles.linkButton} onClick={() => inputRef.current?.click()}>
            choose files
          </button>
        </span>
        <span className={styles.hint}>PDF only · up to 100 MB each</span>
        <input
          ref={inputRef}
          type="file"
          accept="application/pdf,.pdf"
          multiple
          className="visually-hidden"
          onChange={(e) => {
            addFiles(e.target.files)
            e.target.value = ''
          }}
        />
      </div>
      {files.length > 0 && (
        <ul className={styles.files}>
          {files.map((f) => (
            <li key={f.name} className={styles.file}>
              <FileText size={16} className={styles.fileIcon} aria-hidden />
              <span className={styles.fileName}>{f.name}</span>
              <span className={styles.fileSize}>{formatBytes(f.size)}</span>
              <button
                type="button"
                className={styles.remove}
                aria-label={`Remove ${f.name}`}
                onClick={() => setFiles((prev) => prev.filter((x) => x !== f))}
              >
                <X size={14} />
              </button>
            </li>
          ))}
        </ul>
      )}
      {rejected.length > 0 && <p className={styles.error}>{rejected.join(' ')}</p>}
      {submitted && errors.files && <p className={styles.error}>{errors.files}</p>}

      {reference.error ? (
        <ErrorState error={reference.error} onRetry={reference.reload} compact />
      ) : (
        <label className={styles.fieldRow}>
          <span className={styles.fieldLabel}>Document type</span>
          <select className="field" value={typeId} disabled={!reference.data} onChange={(e) => setTypeId(e.target.value)}>
            <option value="">{reference.data ? 'Choose a type…' : 'Loading types…'}</option>
            {types.map((t) => (
              <option key={t.id} value={t.id}>
                {t.name} · {t.code}
              </option>
            ))}
          </select>
        </label>
      )}
      {submitted && errors.type && <p className={styles.error}>{errors.type}</p>}

      {type?.tag === 'Term' && (
        <label className={styles.fieldRow}>
          <span className={styles.fieldLabel}>Academic term</span>
          <select className="field" value={termId} onChange={(e) => setTermChoice(e.target.value)}>
            {years.map((y) =>
              y.terms.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.name} · {y.name}
                </option>
              )),
            )}
          </select>
        </label>
      )}
      {type?.tag === 'Year' && (
        <label className={styles.fieldRow}>
          <span className={styles.fieldLabel}>Academic year</span>
          <select className="field" value={yearId} onChange={(e) => setYearChoice(e.target.value)}>
            {years.map((y) => (
              <option key={y.id} value={y.id}>
                {y.name}
              </option>
            ))}
          </select>
        </label>
      )}
      {type && (
        <div className={styles.tagNote}>
          <TagChip
            tag={type.tag}
            label={type.tag === 'None' ? 'No year or term' : type.tag === 'Term' ? 'Term required' : 'Year required'}
          />
          <span>
            {type.tag === 'None'
              ? `${type.name} documents aren't tagged with a year or term.`
              : `${type.name} documents must be tagged with an academic ${type.tag.toLowerCase()}.`}
          </span>
        </div>
      )}
    </Modal>
  )
}

import { useState } from 'react'
import { Download, FileText } from 'lucide-react'
import { getContentLink, openDocumentContent } from '../../api/library'
import { useApi } from '../../api/useApi'
import { ErrorState, InlineError, Loading } from '../../components/AsyncState'
import { Button } from '../../components/Button'
import { formatBytes } from '../../lib/format'
import styles from './ReviewWorkspacePage.module.css'

interface DocumentCanvasProps {
  documentId: string
  versionId: string
  version: number
  label: string
  fileName: string
  contentType: string
  page: number
  sizeBytes?: number
}

const isPdf = (contentType: string) => contentType === 'application/pdf'
const isImage = (contentType: string) => contentType.startsWith('image/')

/**
 * The file under review, previewed from a short-lived signed link (the browser's PDF viewer; images
 * inline). Other types can only be downloaded. Paging moves the PDF viewer with #page=, which doesn't
 * fetch the file again, so the link expiring later doesn't matter.
 */
export function DocumentCanvas({ documentId, versionId, version, label, fileName, contentType, page, sizeBytes }: DocumentCanvasProps) {
  const previewable = isPdf(contentType) || isImage(contentType)
  const link = useApi(
    () => (previewable ? getContentLink(documentId, { versionId, inline: true }) : Promise.resolve(null)),
    [documentId, versionId, previewable],
  )
  const [downloadError, setDownloadError] = useState<unknown>(undefined)

  const download = () => {
    setDownloadError(undefined)
    openDocumentContent(documentId, { versionId }).catch(setDownloadError)
  }

  if (previewable && link.error) {
    return (
      <div className={styles.canvas}>
        <ErrorState error={link.error} onRetry={link.reload} />
      </div>
    )
  }
  if (previewable && !link.data) {
    return (
      <div className={styles.canvas}>
        <Loading label={`Opening ${fileName}…`} />
      </div>
    )
  }

  if (link.data && isPdf(contentType)) {
    return (
      <div className={`${styles.canvas} ${styles.canvasFrame}`}>
        <iframe className={styles.frame} src={`${link.data.url}#page=${page}`} title={`${label} v${version}, page ${page}`} />
      </div>
    )
  }

  if (link.data && isImage(contentType)) {
    return (
      <div className={styles.canvas}>
        <img className={styles.image} src={link.data.url} alt={`${label} v${version}`} />
      </div>
    )
  }

  return (
    <div className={styles.canvas}>
      <div className={styles.noPreview}>
        <FileText size={28} aria-hidden className={styles.noPreviewIcon} />
        <span className={styles.noPreviewTitle}>{fileName}</span>
        <span className={styles.noPreviewText}>
          This file type can&apos;t be previewed here{sizeBytes ? ` (${formatBytes(sizeBytes)})` : ''}. Download it to review.
        </span>
        <Button icon={Download} onClick={download}>
          Download v{version}
        </Button>
        <InlineError error={downloadError} />
      </div>
    </div>
  )
}

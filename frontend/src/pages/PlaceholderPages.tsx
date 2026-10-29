import { Link } from 'react-router'
import { ScrollText, Search, SearchX } from 'lucide-react'
import { NotDesigned } from '../components/NotDesigned'
import { PageHeader } from '../components/PageHeader'

export function SearchPage() {
  return (
    <>
      <PageHeader breadcrumb="Knowledge base" title="Search" />
      <NotDesigned
        icon={Search}
        title="Search"
        description="Full-text search isn't designed yet. Use the Library search box or ask the AI Assistant."
      />
    </>
  )
}

export function AuditLogPage() {
  return (
    <>
      <PageHeader breadcrumb="Administration" title="Audit log" />
      <NotDesigned icon={ScrollText} title="Audit log" />
    </>
  )
}

export function NotFoundPage() {
  return (
    <>
      <PageHeader title="Page not found" />
      <NotDesigned
        icon={SearchX}
        title="Nothing here"
        description="This address doesn't match any screen."
      />
      <Link to="/">Go to your home page</Link>
    </>
  )
}

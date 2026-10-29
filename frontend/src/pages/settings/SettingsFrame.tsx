import type { ReactNode } from 'react'
import { Building2, CalendarRange, GraduationCap, Layers, ListChecks, Sparkles, Tags, Users } from 'lucide-react'
import { useLocation } from 'react-router'
import { PageHeader } from '../../components/PageHeader'
import { Tabs } from '../../components/Tabs'
import type { TabItem } from '../../components/Tabs'

// Tab order and labels follow frames 17, 19 and 23. The design shows the icon on the active tab only.
const settingsTabs: TabItem[] = [
  { key: 'document-types', label: 'Document types', icon: Tags, to: '/settings/document-types' },
  { key: 'submission-types', label: 'Submission types', icon: Layers, to: '/settings/submission-types' },
  { key: 'required-documents', label: 'Required documents', icon: ListChecks, to: '/settings/required-documents' },
  { key: 'calendar', label: 'Academic calendar', icon: CalendarRange, to: '/settings/calendar' },
  { key: 'org', label: 'Colleges & departments', icon: Building2, to: '/settings/org' },
  { key: 'teaching-loads', label: 'Teaching loads', icon: GraduationCap, to: '/settings/teaching-loads' },
  { key: 'users', label: 'Users & roles', icon: Users, to: '/settings/users' },
  { key: 'ai-search', label: 'AI & search', icon: Sparkles, to: '/settings/ai-search' },
]

interface SettingsFrameProps {
  actions?: ReactNode
  children: ReactNode
}

/** Shared header + tab strip for every Settings page. */
export function SettingsFrame({ actions, children }: SettingsFrameProps) {
  const { pathname } = useLocation()
  const active = settingsTabs.find((tab) => tab.to && pathname.startsWith(tab.to))
  const tabs = settingsTabs.map((tab) => (tab === active ? tab : { ...tab, icon: undefined }))

  return (
    <>
      <PageHeader
        breadcrumb={active ? `Settings / ${active.label}` : 'Settings'}
        title="Settings"
        actions={actions}
      />
      <Tabs items={tabs} />
      {children}
    </>
  )
}

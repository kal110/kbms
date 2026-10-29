import type { ReactNode } from 'react'
import { Building2, GraduationCap } from 'lucide-react'
import { useLocation } from 'react-router'
import { PageHeader } from '../../components/PageHeader'
import { Tabs } from '../../components/Tabs'
import type { TabItem } from '../../components/Tabs'

// The VPAF and admin set both kinds of list from Required documents; an Academic Supervisor sees no tabs.
const requirementTabs: TabItem[] = [
  { key: 'programs', label: 'Programs', icon: GraduationCap, to: '/requirements', end: true },
  { key: 'units', label: 'Colleges & departments', icon: Building2, to: '/requirements/units' },
]

/** Programs | Colleges & departments, with the icon on the active tab only (as in Settings). */
export function RequirementTabs() {
  const { pathname } = useLocation()
  const active = pathname.startsWith('/requirements/units') ? 'units' : 'programs'
  return <Tabs items={requirementTabs.map((tab) => (tab.key === active ? tab : { ...tab, icon: undefined }))} />
}

interface RequirementsFrameProps {
  actions?: ReactNode
  children: ReactNode
}

/** Header + tabs for the Colleges & departments tab, which reuses the Settings editor. */
export function RequirementsFrame({ actions, children }: RequirementsFrameProps) {
  return (
    <>
      <PageHeader breadcrumb="Required documents / Colleges & departments" title="Required documents" actions={actions} />
      <RequirementTabs />
      {children}
    </>
  )
}

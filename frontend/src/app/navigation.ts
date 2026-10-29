import type { LucideIcon } from 'lucide-react'
import {
  ClipboardCheck,
  Cpu,
  FolderOpen,
  LayoutDashboard,
  ListChecks,
  ScrollText,
  Search,
  Send,
  Settings,
  Sparkles,
} from 'lucide-react'
import type { MeDto } from '../api/types'

// The sidebar and home page follow what the signed-in account may do: /api/me returns a persona
// (faculty, supervisor, dean, admin, vpaf, director, staff) and capability flags.

export type PersonaId = 'faculty' | 'supervisor' | 'dean' | 'admin' | 'vpaf' | 'director' | 'staff'

export interface NavItem {
  label: string
  to: string
  icon: LucideIcon
}

export interface NavSection {
  label: string
  items: NavItem[]
}

const library: NavItem = { label: 'Library', to: '/library', icon: FolderOpen }
const search: NavItem = { label: 'Search', to: '/search', icon: Search }
const assistant: NavItem = { label: 'AI Assistant', to: '/assistant', icon: Sparkles }
const dashboard: NavItem = { label: 'Dashboard', to: '/dashboard', icon: LayoutDashboard }
const submissions: NavItem = { label: 'My submissions', to: '/submissions', icon: Send }
const review: NavItem = { label: 'Review queue', to: '/review', icon: ClipboardCheck }
const required: NavItem = { label: 'Required documents', to: '/requirements', icon: ListChecks }
const processing: NavItem = { label: 'Processing & index', to: '/admin/processing', icon: Cpu }
const audit: NavItem = { label: 'Audit log', to: '/admin/audit', icon: ScrollText }
const settings: NavItem = { label: 'Settings', to: '/settings', icon: Settings }

export const can = (me: Pick<MeDto, 'capabilities'>, capability: string) => me.capabilities.includes(capability)

export function navFor(me: MeDto): NavSection[] {
  const has = (capability: string) => can(me, capability)
  const sections: NavSection[] = []

  const kb = [has('canBrowseLibrary') && library, search, assistant].filter(Boolean) as NavItem[]
  sections.push({ label: 'Knowledge base', items: kb })

  const workflow = [
    has('canViewSubmissionDashboard') && dashboard,
    has('canSubmit') && submissions,
    has('canReview') && review,
    // The VPAF and admin set the lists; an Academic Supervisor reads their program's and verifies against it.
    (has('canSetProgramRequirements') || has('canViewProgramRequirements')) && required,
  ].filter(Boolean) as NavItem[]
  if (workflow.length) {
    // Non-academic departments (CICT) have no programs: their people see "Department workflow".
    const office = (has('isStaff') || has('isDepartmentHead')) && !has('isFaculty') && !has('isSupervisor') && !has('isDean')
    sections.push({ label: office ? 'Department workflow' : 'Academic workflow', items: workflow })
  }

  const admin = [has('canViewProcessing') && processing, has('canViewAudit') && audit, has('canManageSettings') && settings].filter(
    Boolean,
  ) as NavItem[]
  if (admin.length) sections.push({ label: 'Administration', items: admin })

  return sections
}

const homes: Record<PersonaId, string> = {
  faculty: '/submissions',
  supervisor: '/review',
  dean: '/dashboard',
  admin: '/library',
  vpaf: '/dashboard',
  director: '/review',
  staff: '/submissions',
}

export const homeFor = (me: Pick<MeDto, 'persona'>) => homes[me.persona as PersonaId] ?? '/library'

/**
 * Dev links: `?as=<account id>` opens the page as that person, e.g. /review?as=ana.lim; `?as=admin` is the
 * seeded System Administrator. In development the account is signed in automatically if it isn't already.
 */
export const demoAccounts: Partial<Record<PersonaId, string>> = {
  admin: 'admin',
}

export const resolveDemoAccount = (value: string | null) => (value ? (demoAccounts[value as PersonaId] ?? value) : null)

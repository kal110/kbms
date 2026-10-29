import { ScopedRequirementsPage } from '../settings/ScopedRequirementsPage'
import { RequirementsFrame } from './RequirementsFrame'

/** Required documents → Colleges & departments: the Settings editor, for the VPAF as well as the admin. */
export function RequirementUnitsPage() {
  return <ScopedRequirementsPage frame={RequirementsFrame} />
}

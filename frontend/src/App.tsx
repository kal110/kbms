import { lazy } from 'react'
import { Navigate, Route, Routes } from 'react-router'
import { AppShell } from './app/AppShell'
import { homeFor } from './app/navigation'
import { RequireSession } from './auth/RequireSession'
import { useMe } from './auth/SessionContext'
import { SignInPage } from './pages/auth/SignInPage'
import { AuditLogPage, NotFoundPage, SearchPage } from './pages/PlaceholderPages'
import { SettingsPlaceholderPage } from './pages/settings/SettingsPlaceholderPage'

// Each screen loads on first visit, so the initial bundle stays small.
const LibraryPage = lazy(() => import('./pages/library/LibraryPage').then((m) => ({ default: m.LibraryPage })))
const AcademicFoldersPage = lazy(() =>
  import('./pages/library/AcademicFoldersPage').then((m) => ({ default: m.AcademicFoldersPage })),
)
const FolderAccessPage = lazy(() =>
  import('./pages/access/FolderAccessPage').then((m) => ({ default: m.FolderAccessPage })),
)
const DocumentDetailPage = lazy(() =>
  import('./pages/documents/DocumentDetailPage').then((m) => ({ default: m.DocumentDetailPage })),
)
const AssistantPage = lazy(() => import('./pages/assistant/AssistantPage').then((m) => ({ default: m.AssistantPage })))
const MySubmissionsPage = lazy(() =>
  import('./pages/submissions/MySubmissionsPage').then((m) => ({ default: m.MySubmissionsPage })),
)
const ReviewWorkspacePage = lazy(() =>
  import('./pages/review/ReviewWorkspacePage').then((m) => ({ default: m.ReviewWorkspacePage })),
)
const RequiredDocumentsPage = lazy(() =>
  import('./pages/requirements/RequiredDocumentsPage').then((m) => ({ default: m.RequiredDocumentsPage })),
)
const RequirementUnitsPage = lazy(() =>
  import('./pages/requirements/RequirementUnitsPage').then((m) => ({ default: m.RequirementUnitsPage })),
)
const SubmissionDashboardPage = lazy(() =>
  import('./pages/dashboard/SubmissionDashboardPage').then((m) => ({ default: m.SubmissionDashboardPage })),
)
const ProcessingPage = lazy(() => import('./pages/admin/ProcessingPage').then((m) => ({ default: m.ProcessingPage })))
const DocumentTypesPage = lazy(() =>
  import('./pages/settings/DocumentTypesPage').then((m) => ({ default: m.DocumentTypesPage })),
)
const SubmissionTypesPage = lazy(() =>
  import('./pages/settings/SubmissionTypesPage').then((m) => ({ default: m.SubmissionTypesPage })),
)
const UsersPage = lazy(() => import('./pages/settings/UsersPage').then((m) => ({ default: m.UsersPage })))
const TeachingLoadsPage = lazy(() =>
  import('./pages/settings/TeachingLoadsPage').then((m) => ({ default: m.TeachingLoadsPage })),
)
const OrgStructurePage = lazy(() =>
  import('./pages/settings/OrgStructurePage').then((m) => ({ default: m.OrgStructurePage })),
)
const ScopedRequirementsPage = lazy(() =>
  import('./pages/settings/ScopedRequirementsPage').then((m) => ({ default: m.ScopedRequirementsPage })),
)
const AcademicCalendarPage = lazy(() =>
  import('./pages/settings/AcademicCalendarPage').then((m) => ({ default: m.AcademicCalendarPage })),
)

function HomeRedirect() {
  return <Navigate to={homeFor(useMe())} replace />
}

export default function App() {
  return (
    <Routes>
      <Route path="signin" element={<SignInPage />} />

      <Route element={<RequireSession />}>
        <Route element={<AppShell />}>
          <Route index element={<HomeRedirect />} />

          <Route path="library" element={<LibraryPage />} />
          <Route path="library/academic" element={<AcademicFoldersPage />} />
          <Route path="library/folders/:folderId/access" element={<FolderAccessPage />} />
          <Route path="documents/:documentId" element={<DocumentDetailPage />} />
          <Route path="search" element={<SearchPage />} />
          <Route path="assistant" element={<AssistantPage />} />

          <Route path="dashboard" element={<SubmissionDashboardPage />} />
          <Route path="submissions" element={<MySubmissionsPage />} />
          <Route path="review" element={<ReviewWorkspacePage />} />
          <Route path="requirements" element={<RequiredDocumentsPage />} />
          <Route path="requirements/units" element={<RequirementUnitsPage />} />
          {/* The college overview was folded into the dashboard's college level. */}
          <Route path="college" element={<Navigate to="/dashboard" replace />} />

          <Route path="admin/processing" element={<ProcessingPage />} />
          <Route path="admin/audit" element={<AuditLogPage />} />

          <Route path="settings">
            <Route index element={<Navigate to="document-types" replace />} />
            <Route path="document-types" element={<DocumentTypesPage />} />
            <Route path="submission-types" element={<SubmissionTypesPage />} />
            <Route path="required-documents" element={<ScopedRequirementsPage />} />
            <Route path="calendar" element={<AcademicCalendarPage />} />
            <Route path="org" element={<OrgStructurePage />} />
            <Route path="teaching-loads" element={<TeachingLoadsPage />} />
            <Route path="users" element={<UsersPage />} />
            <Route path="ai-search" element={<SettingsPlaceholderPage title="AI & search" />} />
          </Route>

          <Route path="*" element={<NotFoundPage />} />
        </Route>
      </Route>
    </Routes>
  )
}

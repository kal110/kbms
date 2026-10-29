import { cached, invalidate } from './cache'
import { api } from './client'
import type {
  AddFacultyRequest,
  AddStaffRequest,
  AssignHeadRequest,
  AssignHolderRequest,
  CourseRefDto,
  CreateCollegeRequest,
  CreateDepartmentRequest,
  CreateProgramRequest,
  CreateUserRequest,
  DepartmentDetailDto,
  ImportTeachingLoadRequest,
  MeDto,
  MyCourseDto,
  OrgCollegeDto,
  OrgStructureDto,
  OrgTreeDto,
  OrgUnitAdminDto,
  PagedResultOfPersonDto,
  PagedResultOfUserAdminDto,
  ProgramDetailDto,
  SetTeachingCourseRequest,
  TeachingImportResultDto,
  TeachingLoadChangeDto,
  TeachingLoadDto,
  UpdateCollegeRequest,
  UpdateDepartmentRequest,
  UpdateProgramRequest,
  UpdateStaffRequest,
  UpdateUserRequest,
  UserAdminDto,
  UserRoleFilter,
} from './types'

export const getMe = (options?: { signal?: AbortSignal; token?: string }) => api.get<MeDto>('/me', options)

/** Colleges → departments → programs, plus non-academic departments (cached per account). */
export const getOrgTree = () => cached('org-tree', () => api.get<OrgTreeDto>('/org/tree'))

export const getProgram = (id: string, signal?: AbortSignal) => api.get<ProgramDetailDto>(`/org/programs/${id}`, { signal })

export const getDepartment = (id: string, signal?: AbortSignal) =>
  api.get<DepartmentDetailDto>(`/org/departments/${id}`, { signal })

export const searchPeople = (q: string, page = 1, pageSize = 25, signal?: AbortSignal) =>
  api.get<PagedResultOfPersonDto>('/org/people', { query: { q, page, pageSize }, signal })

// Admin: who holds each org role. Holders and member counts show in the org tree, so changes drop it.
const orgChanged = <T>(result: T) => {
  invalidate('org-tree')
  return result
}

export const assignDepartmentHead = (departmentId: string, body: AssignHeadRequest) =>
  api.put<DepartmentDetailDto>(`/admin/departments/${departmentId}/head`, body).then(orgChanged)
export const removeDepartmentHead = (departmentId: string) =>
  api.delete<DepartmentDetailDto>(`/admin/departments/${departmentId}/head`).then(orgChanged)
export const addDepartmentStaff = (departmentId: string, body: AddStaffRequest) =>
  api.post<DepartmentDetailDto>(`/admin/departments/${departmentId}/staff`, body).then(orgChanged)
export const updateDepartmentStaff = (departmentId: string, userId: string, body: UpdateStaffRequest) =>
  api.patch<DepartmentDetailDto>(`/admin/departments/${departmentId}/staff/${userId}`, body)
export const removeDepartmentStaff = (departmentId: string, userId: string) =>
  api.delete(`/admin/departments/${departmentId}/staff/${userId}`).then(orgChanged)
export const assignProgramSupervisor = (programId: string, body: AssignHolderRequest) =>
  api.put<ProgramDetailDto>(`/admin/programs/${programId}/supervisor`, body).then(orgChanged)
export const removeProgramSupervisor = (programId: string) =>
  api.delete<ProgramDetailDto>(`/admin/programs/${programId}/supervisor`).then(orgChanged)
export const assignCollegeDean = (collegeId: string, body: AssignHolderRequest) =>
  api.put<OrgCollegeDto>(`/admin/colleges/${collegeId}/dean`, body).then(orgChanged)
export const removeCollegeDean = (collegeId: string) =>
  api.delete<OrgCollegeDto>(`/admin/colleges/${collegeId}/dean`).then(orgChanged)

// Admin: Settings → Users & roles.
export interface UserListQuery {
  q?: string
  role?: NonNullable<UserRoleFilter>
  active?: boolean
  page?: number
  pageSize?: number
}

export const listUsers = (query: UserListQuery, signal?: AbortSignal) =>
  api.get<PagedResultOfUserAdminDto>('/admin/users', { query: { ...query }, signal })
export const getUserForAdmin = (id: string, signal?: AbortSignal) => api.get<UserAdminDto>(`/admin/users/${id}`, { signal })
export const createUser = (body: CreateUserRequest) => api.post<UserAdminDto>('/admin/users', body)
/** Null leaves a field unchanged; "" clears email or title; active false deactivates. */
export const updateUser = (id: string, body: UpdateUserRequest) => api.patch<UserAdminDto>(`/admin/users/${id}`, body)
export const addAdministrator = (userId: string) => api.post<UserAdminDto>('/admin/administrators', { userId })
export const removeAdministrator = (userId: string) => api.delete<UserAdminDto>(`/admin/administrators/${userId}`)
/** Moves the VPAF role to this person. */
export const assignVpaf = (userId: string) => api.put<UserAdminDto>('/admin/vpaf', { userId })
export const removeVpaf = () => api.delete('/admin/vpaf')
export const addFaculty = (programId: string, body: AddFacultyRequest) =>
  api.post<UserAdminDto>(`/admin/programs/${programId}/faculty`, body).then(orgChanged)
export const setPrimaryProgram = (programId: string, userId: string) =>
  api.put<UserAdminDto>(`/admin/programs/${programId}/faculty/${userId}/primary`)
export const removeFaculty = (programId: string, userId: string) =>
  api.delete<UserAdminDto>(`/admin/programs/${programId}/faculty/${userId}`).then(orgChanged)

// Teaching loads: Settings → Teaching loads (admin imports, admin and VPAF read) and the caller's own courses.
export const getTeachingLoad = (termId?: string, signal?: AbortSignal) =>
  api.get<TeachingLoadDto>('/teaching-loads', { query: { termId }, signal })
/** apply false only checks the file; apply true replaces the term's load (refused while the file has errors). */
export const importTeachingLoad = (body: ImportTeachingLoadRequest) =>
  api.post<TeachingImportResultDto>('/teaching-loads/import', body).then((r) => (body.apply ? orgChanged(r) : r))
/** With a per-course submission type, each course carries the caller's live submission of that type. */
export const getMyCourses = (query: { termId?: string; submissionTypeId?: string }, signal?: AbortSignal) =>
  api.get<MyCourseDto[]>('/me/courses', { query: { ...query }, signal })
/** A program's course catalog, for encoding a load by hand. */
export const getProgramCourses = (programId: string, signal?: AbortSignal) =>
  api.get<CourseRefDto[]>('/teaching-loads/courses', { query: { programId }, signal })
/** Sets one faculty member's sections of a course in a term (adds it, edits it, or replaces a course encoded wrongly). */
export const setTeachingCourse = (body: SetTeachingCourseRequest) =>
  api.put<TeachingLoadChangeDto>('/teaching-loads/lines', body).then((r) => (r.membershipAdded ? orgChanged(r) : r))
export const removeTeachingCourse = (termId: string, userId: string, courseId: string) =>
  api.delete<TeachingLoadChangeDto>('/teaching-loads/lines', { query: { termId, userId, courseId } })

// Admin: Settings → Colleges & departments. Every change shows in the org tree, so it drops it.
export const getOrgStructure = (signal?: AbortSignal) => api.get<OrgStructureDto>('/admin/org/structure', { signal })
export const createCollege = (body: CreateCollegeRequest) => api.post<OrgUnitAdminDto>('/admin/colleges', body).then(orgChanged)
export const updateCollege = (id: string, body: UpdateCollegeRequest) =>
  api.patch<OrgUnitAdminDto>(`/admin/colleges/${id}`, body).then(orgChanged)
export const deleteCollege = (id: string) => api.delete(`/admin/colleges/${id}`).then(orgChanged)
export const createDepartment = (body: CreateDepartmentRequest) =>
  api.post<OrgUnitAdminDto>('/admin/departments', body).then(orgChanged)
/** Null leaves a field unchanged; "" clears the short name; collegeId moves an academic department. */
export const updateDepartment = (id: string, body: UpdateDepartmentRequest) =>
  api.patch<OrgUnitAdminDto>(`/admin/departments/${id}`, body).then(orgChanged)
export const deleteDepartment = (id: string) => api.delete(`/admin/departments/${id}`).then(orgChanged)
export const createProgram = (body: CreateProgramRequest) => api.post<OrgUnitAdminDto>('/admin/programs', body).then(orgChanged)
/** departmentId moves the program with its faculty, courses and folders. */
export const updateProgram = (id: string, body: UpdateProgramRequest) =>
  api.patch<OrgUnitAdminDto>(`/admin/programs/${id}`, body).then(orgChanged)
export const deleteProgram = (id: string) => api.delete(`/admin/programs/${id}`).then(orgChanged)

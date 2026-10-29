using KnowledgeBase.Core.Application.Org;
using KnowledgeBase.Core.Common;

namespace KnowledgeBase.Api.Endpoints;

/// <summary>Me, org tree, programs, department head/staff (frames 16, 18, 23), and Settings → Users &amp; roles.</summary>
public static class OrgEndpoints
{
    public static RouteGroupBuilder MapOrgEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me", (OrgService org, CancellationToken ct) => org.GetMeAsync(ct))
            .WithTags("Org")
            .WithSummary("The signed-in user: roles, memberships, persona and capabilities");

        var org = api.MapGroup("/org").WithTags("Org");

        org.MapGet("/tree", (OrgService service, CancellationToken ct) => service.GetTreeAsync(ct))
            .WithSummary("Colleges → departments → programs, plus non-academic departments");

        org.MapGet("/programs/{id:guid}", (Guid id, OrgService service, CancellationToken ct) => service.GetProgramAsync(id, ct))
            .WithSummary("A program with its Academic Supervisor and faculty");

        org.MapGet("/departments/{id:guid}", (Guid id, OrgService service, CancellationToken ct) => service.GetDepartmentAsync(id, ct))
            .WithSummary("A department with its head, staff and programs");

        org.MapGet("/people", (string? q, [AsParameters] PageRequest page, OrgService service, CancellationToken ct) =>
                service.SearchPeopleAsync(q, page, ct))
            .WithSummary("Find active people by name or email");

        var admin = api.MapGroup("/admin").WithTags("Org administration");

        // Settings → Colleges & departments.
        admin.MapGet("/org/structure", (OrgStructureService service, CancellationToken ct) => service.GetAsync(ct))
            .WithSummary("Colleges → departments → programs and non-academic departments, with what each holds");

        admin.MapPost("/colleges", async (CreateCollegeRequest request, OrgStructureService service, CancellationToken ct) =>
            {
                var unit = await service.CreateCollegeAsync(request, ct);
                return TypedResults.Created($"/api/admin/org/structure#{unit.Id}", unit);
            })
            .WithSummary("Add a college (its folder appears under Colleges)");

        admin.MapPatch("/colleges/{id:guid}", (Guid id, UpdateCollegeRequest request, OrgStructureService service, CancellationToken ct) =>
                service.UpdateCollegeAsync(id, request, ct))
            .WithSummary("Rename a college or change its code");

        admin.MapDelete("/colleges/{id:guid}", async (Guid id, OrgStructureService service, CancellationToken ct) =>
            {
                await service.DeleteCollegeAsync(id, ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Delete a college that holds nothing (409 unit-in-use says what's left)");

        admin.MapPost("/departments", async (CreateDepartmentRequest request, OrgStructureService service, CancellationToken ct) =>
            {
                var unit = await service.CreateDepartmentAsync(request, ct);
                return TypedResults.Created($"/api/org/departments/{unit.Id}", unit);
            })
            .WithSummary("Add an academic department (under a college) or a non-academic one");

        admin.MapPatch("/departments/{id:guid}", (Guid id, UpdateDepartmentRequest request, OrgStructureService service, CancellationToken ct) =>
                service.UpdateDepartmentAsync(id, request, ct))
            .WithSummary("Rename a department, or move an academic one to another college");

        admin.MapDelete("/departments/{id:guid}", async (Guid id, OrgStructureService service, CancellationToken ct) =>
            {
                await service.DeleteDepartmentAsync(id, ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Delete a department that holds nothing (409 unit-in-use says what's left)");

        admin.MapPost("/programs", async (CreateProgramRequest request, OrgStructureService service, CancellationToken ct) =>
            {
                var unit = await service.CreateProgramAsync(request, ct);
                return TypedResults.Created($"/api/org/programs/{unit.Id}", unit);
            })
            .WithSummary("Add a program to an academic department");

        admin.MapPatch("/programs/{id:guid}", (Guid id, UpdateProgramRequest request, OrgStructureService service, CancellationToken ct) =>
                service.UpdateProgramAsync(id, request, ct))
            .WithSummary("Rename a program, or move it (with its faculty, courses and folders) to another department");

        admin.MapDelete("/programs/{id:guid}", async (Guid id, OrgStructureService service, CancellationToken ct) =>
            {
                await service.DeleteProgramAsync(id, ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Delete a program that holds nothing (409 unit-in-use says what's left)");

        admin.MapPut("/departments/{id:guid}/head", (Guid id, AssignHeadRequest request, OrgAdminService service, CancellationToken ct) =>
                service.AssignDepartmentHeadAsync(id, request, ct))
            .WithSummary("Assign the Head/Director of a non-academic department");

        admin.MapDelete("/departments/{id:guid}/head", (Guid id, OrgAdminService service, CancellationToken ct) =>
                service.RemoveDepartmentHeadAsync(id, ct))
            .WithSummary("Leave a non-academic department without a head");

        admin.MapPost("/departments/{id:guid}/staff", async (Guid id, AddStaffRequest request, OrgAdminService service, CancellationToken ct) =>
                TypedResults.Created($"/api/org/departments/{id}", await service.AddStaffAsync(id, request, ct)))
            .WithSummary("Add a staff member to a non-academic department");

        admin.MapPatch("/departments/{id:guid}/staff/{userId:guid}",
                (Guid id, Guid userId, UpdateStaffRequest request, OrgAdminService service, CancellationToken ct) =>
                    service.UpdateStaffAsync(id, userId, request, ct))
            .WithSummary("Change a staff member's position");

        admin.MapDelete("/departments/{id:guid}/staff/{userId:guid}", async (Guid id, Guid userId, OrgAdminService service, CancellationToken ct) =>
            {
                await service.RemoveStaffAsync(id, userId, ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Remove a staff member from a department");

        admin.MapPut("/programs/{id:guid}/supervisor", (Guid id, AssignHolderRequest request, OrgAdminService service, CancellationToken ct) =>
                service.AssignProgramSupervisorAsync(id, request, ct))
            .WithSummary("Assign a program's Academic Supervisor");

        admin.MapDelete("/programs/{id:guid}/supervisor", (Guid id, OrgAdminService service, CancellationToken ct) =>
                service.RemoveProgramSupervisorAsync(id, ct))
            .WithSummary("Leave a program without an Academic Supervisor (reviews fall back to the Dean)");

        admin.MapPut("/colleges/{id:guid}/dean", (Guid id, AssignHolderRequest request, OrgAdminService service, CancellationToken ct) =>
                service.AssignCollegeDeanAsync(id, request, ct))
            .WithSummary("Assign a college's Dean");

        admin.MapDelete("/colleges/{id:guid}/dean", (Guid id, OrgAdminService service, CancellationToken ct) =>
                service.RemoveCollegeDeanAsync(id, ct))
            .WithSummary("Leave a college without a Dean");

        // Settings → Users & roles.
        admin.MapGet("/users", (string? q, UserRoleFilter? role, bool? active, [AsParameters] PageRequest page,
                UserAdminService service, CancellationToken ct) => service.ListAsync(q, role, active, page, ct))
            .WithSummary("People with their roles and memberships, filtered by name, role or status");

        admin.MapGet("/users/{id:guid}", (Guid id, UserAdminService service, CancellationToken ct) => service.GetAsync(id, ct))
            .WithSummary("A person with their roles and memberships");

        admin.MapPost("/users", async (CreateUserRequest request, UserAdminService service, CancellationToken ct) =>
            {
                var user = await service.CreateAsync(request, ct);
                return TypedResults.Created($"/api/admin/users/{user.Id}", user);
            })
            .WithSummary("Register a person by their sign-in id");

        admin.MapPatch("/users/{id:guid}", (Guid id, UpdateUserRequest request, UserAdminService service, CancellationToken ct) =>
                service.UpdateAsync(id, request, ct))
            .WithSummary("Edit a person, or deactivate / reactivate their account");

        admin.MapPost("/administrators", (AssignHolderRequest request, UserAdminService service, CancellationToken ct) =>
                service.AddAdministratorAsync(request, ct))
            .WithSummary("Make a person a System Administrator");

        admin.MapDelete("/administrators/{userId:guid}", (Guid userId, UserAdminService service, CancellationToken ct) =>
                service.RemoveAdministratorAsync(userId, ct))
            .WithSummary("Remove a System Administrator (not yourself)");

        admin.MapPut("/vpaf", (AssignHolderRequest request, UserAdminService service, CancellationToken ct) =>
                service.AssignVpafAsync(request, ct))
            .WithSummary("Assign the VP for Academic Affairs (replaces the current holder)");

        admin.MapDelete("/vpaf", async (UserAdminService service, CancellationToken ct) =>
            {
                await service.RemoveVpafAsync(ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Leave the VPAF role vacant");

        admin.MapPost("/programs/{id:guid}/faculty", (Guid id, AddFacultyRequest request, UserAdminService service, CancellationToken ct) =>
                service.AddFacultyAsync(id, request, ct))
            .WithSummary("Add a person to a program's faculty");

        admin.MapPut("/programs/{id:guid}/faculty/{userId:guid}/primary", (Guid id, Guid userId, UserAdminService service, CancellationToken ct) =>
                service.SetPrimaryProgramAsync(id, userId, ct))
            .WithSummary("Make this program the faculty member's primary program");

        admin.MapDelete("/programs/{id:guid}/faculty/{userId:guid}", (Guid id, Guid userId, UserAdminService service, CancellationToken ct) =>
                service.RemoveFacultyAsync(id, userId, ct))
            .WithSummary("Remove a person from a program's faculty (their folder and documents stay)");

        var loads = api.MapGroup("/teaching-loads").WithTags("Teaching loads");

        loads.MapGet("", (Guid? termId, TeachingLoadService service, CancellationToken ct) => service.GetAsync(termId, ct))
            .WithSummary("A term's teaching load (the active term by default); administrators and the VPAF");

        loads.MapPost("/import", (ImportTeachingLoadRequest request, TeachingLoadService service, CancellationToken ct) =>
                service.ImportAsync(request, ct))
            .WithSummary("Check a CSV or Excel teaching load (Apply false), or replace the term's load with it (Apply true)");

        loads.MapGet("/courses", (Guid programId, TeachingLoadService service, CancellationToken ct) =>
                service.GetCoursesAsync(programId, ct))
            .WithSummary("A program's course catalog, for encoding a load by hand");

        loads.MapPut("/lines", (SetTeachingCourseRequest request, TeachingLoadService service, CancellationToken ct) =>
                service.SetCourseAsync(request, ct))
            .WithSummary("Encode a faculty member's course in a term: add it to their load, set its sections, or replace a course encoded wrongly");

        loads.MapDelete("/lines", (Guid termId, Guid userId, Guid courseId, TeachingLoadService service, CancellationToken ct) =>
                service.RemoveCourseAsync(termId, userId, courseId, ct))
            .WithSummary("Take a course (every section) out of a faculty member's load for a term");

        api.MapGet("/me/courses", (Guid? termId, Guid? submissionTypeId, TeachingLoadService service, CancellationToken ct) =>
                service.GetMyCoursesAsync(termId, submissionTypeId, ct))
            .WithTags("Teaching loads")
            .WithSummary("The courses I teach in a term, with my submission of a per-course type for each");

        return api;
    }
}

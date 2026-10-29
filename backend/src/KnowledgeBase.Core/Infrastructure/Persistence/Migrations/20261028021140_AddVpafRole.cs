using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeBase.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVpafRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_role_assignments_scope",
                table: "role_assignments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_role_assignments_scope",
                table: "role_assignments",
                sql: "(role IN ('SystemAdmin', 'Vpaf') AND scope_type = 'Global' AND scope_id IS NULL) OR (role = 'Dean' AND scope_type = 'College' AND scope_id IS NOT NULL) OR (role = 'AcademicSupervisor' AND scope_type = 'Program' AND scope_id IS NOT NULL) OR (role = 'DepartmentHead' AND scope_type = 'Department' AND scope_id IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The old constraint has no Vpaf: those assignments go with it.
            migrationBuilder.Sql("DELETE FROM role_assignments WHERE role = 'Vpaf';");

            migrationBuilder.DropCheckConstraint(
                name: "ck_role_assignments_scope",
                table: "role_assignments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_role_assignments_scope",
                table: "role_assignments",
                sql: "(role = 'SystemAdmin' AND scope_type = 'Global' AND scope_id IS NULL) OR (role = 'Dean' AND scope_type = 'College' AND scope_id IS NOT NULL) OR (role = 'AcademicSupervisor' AND scope_type = 'Program' AND scope_id IS NOT NULL) OR (role = 'DepartmentHead' AND scope_type = 'Department' AND scope_id IS NOT NULL)");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeBase.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTeachingLoad : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "course_id",
                table: "submissions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "per_course",
                table: "submission_types",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "courses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_courses", x => x.id);
                    table.ForeignKey(
                        name: "fk_courses_programs_program_id",
                        column: x => x.program_id,
                        principalTable: "academic_programs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "teaching_load_imports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    academic_term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    imported_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    rows = table.Column<int>(type: "integer", nullable: false),
                    added = table.Column<int>(type: "integer", nullable: false),
                    removed = table.Column<int>(type: "integer", nullable: false),
                    people_created = table.Column<int>(type: "integer", nullable: false),
                    courses_created = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_teaching_load_imports", x => x.id);
                    table.ForeignKey(
                        name: "fk_teaching_load_imports_academic_terms_academic_term_id",
                        column: x => x.academic_term_id,
                        principalTable: "academic_terms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_teaching_load_imports_users_imported_by_user_id",
                        column: x => x.imported_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "teaching_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    academic_term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    course_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_teaching_assignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_teaching_assignments_academic_terms_academic_term_id",
                        column: x => x.academic_term_id,
                        principalTable: "academic_terms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_teaching_assignments_courses_course_id",
                        column: x => x.course_id,
                        principalTable: "courses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_teaching_assignments_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_submissions_course_id_academic_term_id",
                table: "submissions",
                columns: new[] { "course_id", "academic_term_id" });

            migrationBuilder.CreateIndex(
                name: "ix_courses_program_id_code",
                table: "courses",
                columns: new[] { "program_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_teaching_assignments_academic_term_id_course_id",
                table: "teaching_assignments",
                columns: new[] { "academic_term_id", "course_id" });

            migrationBuilder.CreateIndex(
                name: "ix_teaching_assignments_academic_term_id_user_id_course_id_sec",
                table: "teaching_assignments",
                columns: new[] { "academic_term_id", "user_id", "course_id", "section" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_teaching_assignments_course_id",
                table: "teaching_assignments",
                column: "course_id");

            migrationBuilder.CreateIndex(
                name: "ix_teaching_assignments_user_id",
                table: "teaching_assignments",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_teaching_load_imports_academic_term_id_imported_at",
                table: "teaching_load_imports",
                columns: new[] { "academic_term_id", "imported_at" });

            migrationBuilder.CreateIndex(
                name: "ix_teaching_load_imports_imported_by_user_id",
                table: "teaching_load_imports",
                column: "imported_by_user_id");

            migrationBuilder.AddForeignKey(
                name: "fk_submissions_courses_course_id",
                table: "submissions",
                column: "course_id",
                principalTable: "courses",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_submissions_courses_course_id",
                table: "submissions");

            migrationBuilder.DropTable(
                name: "teaching_assignments");

            migrationBuilder.DropTable(
                name: "teaching_load_imports");

            migrationBuilder.DropTable(
                name: "courses");

            migrationBuilder.DropIndex(
                name: "ix_submissions_course_id_academic_term_id",
                table: "submissions");

            migrationBuilder.DropColumn(
                name: "course_id",
                table: "submissions");

            migrationBuilder.DropColumn(
                name: "per_course",
                table: "submission_types");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace KnowledgeBase.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateTable(
                name: "academic_years",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: false),
                    ends_on = table.Column<DateOnly>(type: "date", nullable: false),
                    from_sis = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_academic_years", x => x.id);
                    table.CheckConstraint("ck_academic_years_dates", "starts_on < ends_on");
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    resource_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ip_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    metadata = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "collections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    inherit_permissions = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "colleges",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_colleges", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document_permissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    resource_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    principal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    level = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    granted_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_permissions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "folders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    path = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    org_ref_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    inherit_permissions = table.Column<bool>(type: "boolean", nullable: false),
                    is_restricted = table.Column<bool>(type: "boolean", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_folders", x => x.id);
                    table.ForeignKey(
                        name: "fk_folders_folders_parent_id",
                        column: x => x.parent_id,
                        principalTable: "folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    route = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status_code = table.Column<int>(type: "integer", nullable: false),
                    response_body = table.Column<string>(type: "text", nullable: true),
                    response_content_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => new { x.user_id, x.key, x.route });
                });

            migrationBuilder.CreateTable(
                name: "index_builds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    collection = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    model = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    dimensions = table.Column<int>(type: "integer", nullable: false),
                    pipeline_version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    progress = table.Column<double>(type: "double precision", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_index_builds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    payload = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "retention_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    retain_years = table.Column<int>(type: "integer", nullable: true),
                    trigger = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    action_after = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retention_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "submission_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    program_scoped = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_submission_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tags", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "upload_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    object_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    content_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    expected_size = table.Column<long>(type: "bigint", nullable: false),
                    expected_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    s3upload_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_upload_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "academic_terms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    academic_year_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    short_name = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    starts_on = table.Column<DateOnly>(type: "date", nullable: false),
                    ends_on = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    submission_opens_on = table.Column<DateOnly>(type: "date", nullable: true),
                    submission_closes_on = table.Column<DateOnly>(type: "date", nullable: true),
                    allow_resubmit_after_close = table.Column<bool>(type: "boolean", nullable: false),
                    from_sis = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_academic_terms", x => x.id);
                    table.CheckConstraint("ck_academic_terms_dates", "starts_on < ends_on");
                    table.CheckConstraint("ck_academic_terms_window", "(submission_opens_on IS NULL OR submission_opens_on >= starts_on) AND (submission_closes_on IS NULL OR submission_closes_on <= ends_on) AND (submission_opens_on IS NULL OR submission_closes_on IS NULL OR submission_opens_on <= submission_closes_on)");
                    table.ForeignKey(
                        name: "fk_academic_terms_academic_years_academic_year_id",
                        column: x => x.academic_year_id,
                        principalTable: "academic_years",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "departments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    short_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    college_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_departments", x => x.id);
                    table.CheckConstraint("ck_departments_kind_college", "(kind = 'Academic' AND college_id IS NOT NULL) OR (kind = 'NonAcademic' AND college_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_departments_colleges_college_id",
                        column: x => x.college_id,
                        principalTable: "colleges",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "folder_effective_access",
                columns: table => new
                {
                    folder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal_key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    level = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    source_folder_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_folder_effective_access", x => new { x.folder_id, x.principal_key });
                    table.ForeignKey(
                        name: "fk_folder_effective_access_folders_folder_id",
                        column: x => x.folder_id,
                        principalTable: "folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    number_pattern = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    academic_tag = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    scope = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    retention_policy_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    replaced_by_type_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_types", x => x.id);
                    table.ForeignKey(
                        name: "fk_document_types_document_types_replaced_by_type_id",
                        column: x => x.replaced_by_type_id,
                        principalTable: "document_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_document_types_retention_policies_retention_policy_id",
                        column: x => x.retention_policy_id,
                        principalTable: "retention_policies",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "approval_stages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_order = table.Column<int>(type: "integer", nullable: false),
                    reviewer_rule = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approval_stages", x => x.id);
                    table.ForeignKey(
                        name: "fk_approval_stages_submission_types_submission_type_id",
                        column: x => x.submission_type_id,
                        principalTable: "submission_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "requirement_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    effective_from_term_id = table.Column<Guid>(type: "uuid", nullable: true),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    published_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_requirement_sets", x => x.id);
                    table.ForeignKey(
                        name: "fk_requirement_sets_submission_types_submission_type_id",
                        column: x => x.submission_type_id,
                        principalTable: "submission_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "group_members",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_members", x => new { x.group_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_group_members_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_group_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    link = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    scope_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_assignments", x => x.id);
                    table.CheckConstraint("ck_role_assignments_scope", "(role = 'SystemAdmin' AND scope_type = 'Global' AND scope_id IS NULL) OR (role = 'Dean' AND scope_type = 'College' AND scope_id IS NOT NULL) OR (role = 'AcademicSupervisor' AND scope_type = 'Program' AND scope_id IS NOT NULL) OR (role = 'DepartmentHead' AND scope_type = 'Department' AND scope_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_role_assignments_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "academic_programs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_academic_programs", x => x.id);
                    table.ForeignKey(
                        name: "fk_academic_programs_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "department_staff",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_department_staff", x => new { x.user_id, x.department_id });
                    table.ForeignKey(
                        name: "fk_department_staff_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_department_staff_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_number_sequences",
                columns: table => new
                {
                    document_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    next_value = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_number_sequences", x => new { x.document_type_id, x.year });
                    table.ForeignKey(
                        name: "fk_document_number_sequences_document_types_document_type_id",
                        column: x => x.document_type_id,
                        principalTable: "document_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "submission_requirements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    requirement_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    document_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    min_files = table.Column<int>(type: "integer", nullable: false),
                    max_files = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    due_on = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_submission_requirements", x => x.id);
                    table.CheckConstraint("ck_submission_requirements_files", "min_files >= 0 AND max_files >= 1 AND max_files >= min_files");
                    table.ForeignKey(
                        name: "fk_submission_requirements_document_types_document_type_id",
                        column: x => x.document_type_id,
                        principalTable: "document_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_submission_requirements_requirement_sets_requirement_set_id",
                        column: x => x.requirement_set_id,
                        principalTable: "requirement_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "faculty_programs",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_faculty_programs", x => new { x.user_id, x.program_id });
                    table.ForeignKey(
                        name: "fk_faculty_programs_programs_program_id",
                        column: x => x.program_id,
                        principalTable: "academic_programs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_faculty_programs_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "submissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    submitted_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    program_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    academic_term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    academic_year_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    assigned_reviewer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_stage = table.Column<int>(type: "integer", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finalized_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_submissions", x => x.id);
                    table.CheckConstraint("ck_submissions_status", "status IN ('Draft', 'Submitted', 'UnderReview', 'RevisionRequested', 'Resubmitted', 'Approved', 'Rejected', 'Withdrawn')");
                    table.CheckConstraint("ck_submissions_unit", "program_id IS NOT NULL OR department_id IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_submissions_academic_terms_academic_term_id",
                        column: x => x.academic_term_id,
                        principalTable: "academic_terms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_submissions_academic_years_academic_year_id",
                        column: x => x.academic_year_id,
                        principalTable: "academic_years",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_submissions_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_submissions_programs_program_id",
                        column: x => x.program_id,
                        principalTable: "academic_programs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_submissions_submission_types_submission_type_id",
                        column: x => x.submission_type_id,
                        principalTable: "submission_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_submissions_users_assigned_reviewer_id",
                        column: x => x.assigned_reviewer_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_submissions_users_submitted_by_user_id",
                        column: x => x.submitted_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "review_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    stage_order = table.Column<int>(type: "integer", nullable: false),
                    previous_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    new_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    comments = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    document_version_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_actions", x => x.id);
                    table.CheckConstraint("ck_review_actions_comment", "action_type NOT IN ('RevisionRequested', 'Rejected') OR (comments IS NOT NULL AND length(btrim(comments)) > 0)");
                    table.ForeignKey(
                        name: "fk_review_actions_submissions_submission_id",
                        column: x => x.submission_id,
                        principalTable: "submissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_review_actions_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "submission_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stage_order = table.Column<int>(type: "integer", nullable: false),
                    reviewer_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_submission_assignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_submission_assignments_submissions_submission_id",
                        column: x => x.submission_id,
                        principalTable: "submissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_submission_assignments_users_reviewer_user_id",
                        column: x => x.reviewer_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "submission_requirement_sets",
                columns: table => new
                {
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requirement_set_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_submission_requirement_sets", x => new { x.submission_id, x.requirement_set_id });
                    table.ForeignKey(
                        name: "fk_submission_requirement_sets_requirement_sets_requirement_se",
                        column: x => x.requirement_set_id,
                        principalTable: "requirement_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_submission_requirement_sets_submissions_submission_id",
                        column: x => x.submission_id,
                        principalTable: "submissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "collection_documents",
                columns: table => new
                {
                    collection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collection_documents", x => new { x.collection_id, x.document_id });
                    table.ForeignKey(
                        name: "fk_collection_documents_collections_collection_id",
                        column: x => x.collection_id,
                        principalTable: "collections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_tags",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_tags", x => new { x.document_id, x.tag_id });
                    table.ForeignKey(
                        name: "fk_document_tags_tags_tag_id",
                        column: x => x.tag_id,
                        principalTable: "tags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    object_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    original_filename = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    content_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    file_size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    page_count = table.Column<int>(type: "integer", nullable: true),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    change_description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    processing_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    text_object_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_from_version_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_versions", x => x.id);
                    table.UniqueConstraint("ak_document_versions_document_id_id", x => new { x.document_id, x.id });
                    table.CheckConstraint("ck_document_versions_number", "version_number >= 1");
                    table.CheckConstraint("ck_document_versions_sha256", "sha256 ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_document_versions_size", "file_size_bytes > 0");
                    table.ForeignKey(
                        name: "fk_document_versions_document_versions_created_from_version_id",
                        column: x => x.created_from_version_id,
                        principalTable: "document_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    document_number = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    document_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    folder_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    academic_year_id = table.Column<Guid>(type: "uuid", nullable: true),
                    academic_term_id = table.Column<Guid>(type: "uuid", nullable: true),
                    current_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    published_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lifecycle_status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    inherit_permissions = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "to_tsvector('simple', coalesce(title, '') || ' ' || coalesce(document_number, '') || ' ' || coalesce(description, ''))", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_documents_academic_terms_academic_term_id",
                        column: x => x.academic_term_id,
                        principalTable: "academic_terms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_documents_academic_years_academic_year_id",
                        column: x => x.academic_year_id,
                        principalTable: "academic_years",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_documents_document_types_document_type_id",
                        column: x => x.document_type_id,
                        principalTable: "document_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_documents_document_versions_current_version_id",
                        column: x => x.current_version_id,
                        principalTable: "document_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_documents_document_versions_published_version_id",
                        column: x => x.published_version_id,
                        principalTable: "document_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_documents_folders_folder_id",
                        column: x => x.folder_id,
                        principalTable: "folders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_documents_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "processing_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_version = table.Column<int>(type: "integer", nullable: false),
                    stage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    metrics = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processing_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_processing_jobs_document_versions_document_version_id",
                        column: x => x.document_version_id,
                        principalTable: "document_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "legal_holds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    placed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    placed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_legal_holds", x => x.id);
                    table.ForeignKey(
                        name: "fk_legal_holds_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "publication_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_publication_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_publication_records_document_versions_document_version_id",
                        column: x => x.document_version_id,
                        principalTable: "document_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_publication_records_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "submission_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    submission_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requirement_key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_submission_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_submission_documents_document_versions_document_id_document",
                        columns: x => new { x.document_id, x.document_version_id },
                        principalTable: "document_versions",
                        principalColumns: new[] { "document_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_submission_documents_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_submission_documents_submissions_submission_id",
                        column: x => x.submission_id,
                        principalTable: "submissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_academic_programs_code",
                table: "academic_programs",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_academic_programs_department_id",
                table: "academic_programs",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_academic_terms_academic_year_id",
                table: "academic_terms",
                column: "academic_year_id");

            migrationBuilder.CreateIndex(
                name: "ix_academic_terms_code",
                table: "academic_terms",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_academic_terms_one_active",
                table: "academic_terms",
                column: "status",
                unique: true,
                filter: "status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_academic_years_code",
                table: "academic_years",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_approval_stages_submission_type_id_stage_order",
                table: "approval_stages",
                columns: new[] { "submission_type_id", "stage_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_actor_user_id_occurred_at",
                table: "audit_events",
                columns: new[] { "actor_user_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_occurred_at",
                table: "audit_events",
                column: "occurred_at")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_resource_type_resource_id",
                table: "audit_events",
                columns: new[] { "resource_type", "resource_id" });

            migrationBuilder.CreateIndex(
                name: "ix_collection_documents_document_id",
                table: "collection_documents",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_collections_name",
                table: "collections",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_colleges_code",
                table: "colleges",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_department_staff_department_id",
                table: "department_staff",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_departments_code",
                table: "departments",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_departments_college_id",
                table: "departments",
                column: "college_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_permissions_principal_key",
                table: "document_permissions",
                column: "principal_key");

            migrationBuilder.CreateIndex(
                name: "ix_document_permissions_resource_type_resource_id_principal_key",
                table: "document_permissions",
                columns: new[] { "resource_type", "resource_id", "principal_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_tags_tag_id",
                table: "document_tags",
                column: "tag_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_types_code",
                table: "document_types",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_types_replaced_by_type_id",
                table: "document_types",
                column: "replaced_by_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_types_retention_policy_id",
                table: "document_types",
                column: "retention_policy_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_versions_created_from_version_id",
                table: "document_versions",
                column: "created_from_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_versions_document_id_version_number",
                table: "document_versions",
                columns: new[] { "document_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_versions_object_key",
                table: "document_versions",
                column: "object_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_documents_academic_term_id",
                table: "documents",
                column: "academic_term_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_academic_year_id",
                table: "documents",
                column: "academic_year_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_current_version_id",
                table: "documents",
                column: "current_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_document_type_id_document_number",
                table: "documents",
                columns: new[] { "document_type_id", "document_number" },
                unique: true,
                filter: "document_number IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_documents_folder_id_lifecycle_status_updated_at",
                table: "documents",
                columns: new[] { "folder_id", "lifecycle_status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_documents_number_trgm",
                table: "documents",
                column: "document_number")
                .Annotation("Npgsql:IndexMethod", "GIN")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_documents_owner_user_id",
                table: "documents",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_published_version_id",
                table: "documents",
                column: "published_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_search_vector",
                table: "documents",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "ix_documents_title_trgm",
                table: "documents",
                column: "title")
                .Annotation("Npgsql:IndexMethod", "GIN")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_faculty_programs_program_id",
                table: "faculty_programs",
                column: "program_id");

            migrationBuilder.CreateIndex(
                name: "ix_folder_effective_access_principal_key_level",
                table: "folder_effective_access",
                columns: new[] { "principal_key", "level" });

            migrationBuilder.CreateIndex(
                name: "ix_folders_parent_id",
                table: "folders",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_folders_path",
                table: "folders",
                column: "path")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "ux_folders_system",
                table: "folders",
                columns: new[] { "parent_id", "kind", "org_ref_id" },
                unique: true,
                filter: "is_system");

            migrationBuilder.CreateIndex(
                name: "ix_group_members_user_id",
                table: "group_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_groups_external_id",
                table: "groups",
                column: "external_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_created_at",
                table: "idempotency_records",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_legal_holds_document_id",
                table: "legal_holds",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_user_id_read_at_created_at",
                table: "notifications",
                columns: new[] { "user_id", "read_at", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_unsent",
                table: "outbox_messages",
                column: "occurred_at",
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_processing_jobs_document_version_id_pipeline_version",
                table: "processing_jobs",
                columns: new[] { "document_version_id", "pipeline_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_processing_jobs_status_next_attempt_at",
                table: "processing_jobs",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_publication_records_document_id_created_at",
                table: "publication_records",
                columns: new[] { "document_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_publication_records_document_version_id",
                table: "publication_records",
                column: "document_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_requirement_sets_scope_type_scope_id_submission_type_id_set",
                table: "requirement_sets",
                columns: new[] { "scope_type", "scope_id", "submission_type_id", "set_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_requirement_sets_submission_type_id",
                table: "requirement_sets",
                column: "submission_type_id");

            migrationBuilder.CreateIndex(
                name: "ux_requirement_sets_one_draft",
                table: "requirement_sets",
                columns: new[] { "scope_type", "scope_id", "submission_type_id" },
                unique: true,
                filter: "status = 'Draft'");

            migrationBuilder.CreateIndex(
                name: "ix_retention_policies_name",
                table: "retention_policies",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_review_actions_actor_user_id",
                table: "review_actions",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_actions_submission_id_occurred_at",
                table: "review_actions",
                columns: new[] { "submission_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_role_assignments_role_scope_id",
                table: "role_assignments",
                columns: new[] { "role", "scope_id" },
                unique: true,
                filter: "is_active AND role IN ('Dean', 'AcademicSupervisor', 'DepartmentHead')");

            migrationBuilder.CreateIndex(
                name: "ix_role_assignments_user_id",
                table: "role_assignments",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_submission_assignments_reviewer_user_id_completed_at",
                table: "submission_assignments",
                columns: new[] { "reviewer_user_id", "completed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_submission_assignments_submission_id_stage_order",
                table: "submission_assignments",
                columns: new[] { "submission_id", "stage_order" });

            migrationBuilder.CreateIndex(
                name: "ix_submission_documents_document_id_document_version_id",
                table: "submission_documents",
                columns: new[] { "document_id", "document_version_id" });

            migrationBuilder.CreateIndex(
                name: "ix_submission_documents_submission_id_document_version_id",
                table: "submission_documents",
                columns: new[] { "submission_id", "document_version_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_submission_documents_submission_id_requirement_key",
                table: "submission_documents",
                columns: new[] { "submission_id", "requirement_key" });

            migrationBuilder.CreateIndex(
                name: "ix_submission_requirement_sets_requirement_set_id",
                table: "submission_requirement_sets",
                column: "requirement_set_id");

            migrationBuilder.CreateIndex(
                name: "ix_submission_requirements_document_type_id",
                table: "submission_requirements",
                column: "document_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_submission_requirements_requirement_set_id_key",
                table: "submission_requirements",
                columns: new[] { "requirement_set_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_submission_types_code",
                table: "submission_types",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_submissions_academic_term_id",
                table: "submissions",
                column: "academic_term_id");

            migrationBuilder.CreateIndex(
                name: "ix_submissions_academic_year_id",
                table: "submissions",
                column: "academic_year_id");

            migrationBuilder.CreateIndex(
                name: "ix_submissions_assigned_reviewer_id_status_submitted_at",
                table: "submissions",
                columns: new[] { "assigned_reviewer_id", "status", "submitted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_submissions_department_id",
                table: "submissions",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_submissions_program_id_academic_term_id",
                table: "submissions",
                columns: new[] { "program_id", "academic_term_id" });

            migrationBuilder.CreateIndex(
                name: "ix_submissions_submission_number",
                table: "submissions",
                column: "submission_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_submissions_submission_type_id",
                table: "submissions",
                column: "submission_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_submissions_submitted_by_user_id_status",
                table: "submissions",
                columns: new[] { "submitted_by_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_tags_name",
                table: "tags",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_upload_sessions_object_key",
                table: "upload_sessions",
                column: "object_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_upload_sessions_user_id_status",
                table: "upload_sessions",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_users_external_id",
                table: "users",
                column: "external_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_collection_documents_documents_document_id",
                table: "collection_documents",
                column: "document_id",
                principalTable: "documents",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_document_tags_documents_document_id",
                table: "document_tags",
                column: "document_id",
                principalTable: "documents",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_document_versions_documents_document_id",
                table: "document_versions",
                column: "document_id",
                principalTable: "documents",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Append-only history, immutable files, period tags that match the type.
            migrationBuilder.Sql(DatabaseTriggers.Create);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DatabaseTriggers.Drop);

            migrationBuilder.DropForeignKey(
                name: "fk_academic_terms_academic_years_academic_year_id",
                table: "academic_terms");

            migrationBuilder.DropForeignKey(
                name: "fk_documents_academic_years_academic_year_id",
                table: "documents");

            migrationBuilder.DropForeignKey(
                name: "fk_document_versions_documents_document_id",
                table: "document_versions");

            migrationBuilder.DropTable(
                name: "approval_stages");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "collection_documents");

            migrationBuilder.DropTable(
                name: "department_staff");

            migrationBuilder.DropTable(
                name: "document_number_sequences");

            migrationBuilder.DropTable(
                name: "document_permissions");

            migrationBuilder.DropTable(
                name: "document_tags");

            migrationBuilder.DropTable(
                name: "faculty_programs");

            migrationBuilder.DropTable(
                name: "folder_effective_access");

            migrationBuilder.DropTable(
                name: "group_members");

            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "index_builds");

            migrationBuilder.DropTable(
                name: "legal_holds");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "processing_jobs");

            migrationBuilder.DropTable(
                name: "publication_records");

            migrationBuilder.DropTable(
                name: "review_actions");

            migrationBuilder.DropTable(
                name: "role_assignments");

            migrationBuilder.DropTable(
                name: "submission_assignments");

            migrationBuilder.DropTable(
                name: "submission_documents");

            migrationBuilder.DropTable(
                name: "submission_requirement_sets");

            migrationBuilder.DropTable(
                name: "submission_requirements");

            migrationBuilder.DropTable(
                name: "upload_sessions");

            migrationBuilder.DropTable(
                name: "collections");

            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.DropTable(
                name: "groups");

            migrationBuilder.DropTable(
                name: "submissions");

            migrationBuilder.DropTable(
                name: "requirement_sets");

            migrationBuilder.DropTable(
                name: "academic_programs");

            migrationBuilder.DropTable(
                name: "submission_types");

            migrationBuilder.DropTable(
                name: "departments");

            migrationBuilder.DropTable(
                name: "colleges");

            migrationBuilder.DropTable(
                name: "academic_years");

            migrationBuilder.DropTable(
                name: "documents");

            migrationBuilder.DropTable(
                name: "academic_terms");

            migrationBuilder.DropTable(
                name: "document_types");

            migrationBuilder.DropTable(
                name: "document_versions");

            migrationBuilder.DropTable(
                name: "folders");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "retention_policies");
        }
    }
}

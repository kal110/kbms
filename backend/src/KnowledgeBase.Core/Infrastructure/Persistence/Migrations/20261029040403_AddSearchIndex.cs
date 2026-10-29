using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KnowledgeBase.Core.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "indexed_versions",
                table: "index_builds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "last_error",
                table: "index_builds",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "requested_by_user_id",
                table: "index_builds",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "total_versions",
                table: "index_builds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ai_query_audits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    filters = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    chunk_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    cited_document_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    question_text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    answer_text = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_query_audits", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "version_index_entries",
                columns: table => new
                {
                    collection = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pipeline_version = table.Column<int>(type: "integer", nullable: false),
                    chunk_count = table.Column<int>(type: "integer", nullable: false),
                    token_count = table.Column<int>(type: "integer", nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    indexed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_version_index_entries", x => new { x.collection, x.version_id });
                    table.ForeignKey(
                        name: "fk_version_index_entries_document_versions_version_id",
                        column: x => x.version_id,
                        principalTable: "document_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_index_builds_collection",
                table: "index_builds",
                column: "collection",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_index_builds_in_flight",
                table: "index_builds",
                column: "status",
                unique: true,
                filter: "status IN ('Pending', 'Building')");

            migrationBuilder.CreateIndex(
                name: "ix_ai_query_audits_asked_at",
                table: "ai_query_audits",
                column: "asked_at")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "ix_ai_query_audits_user_id_asked_at",
                table: "ai_query_audits",
                columns: new[] { "user_id", "asked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_version_index_entries_document_id",
                table: "version_index_entries",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_version_index_entries_version_id",
                table: "version_index_entries",
                column: "version_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_query_audits");

            migrationBuilder.DropTable(
                name: "version_index_entries");

            migrationBuilder.DropIndex(
                name: "ix_index_builds_collection",
                table: "index_builds");

            migrationBuilder.DropIndex(
                name: "ux_index_builds_in_flight",
                table: "index_builds");

            migrationBuilder.DropColumn(
                name: "indexed_versions",
                table: "index_builds");

            migrationBuilder.DropColumn(
                name: "last_error",
                table: "index_builds");

            migrationBuilder.DropColumn(
                name: "requested_by_user_id",
                table: "index_builds");

            migrationBuilder.DropColumn(
                name: "total_versions",
                table: "index_builds");
        }
    }
}

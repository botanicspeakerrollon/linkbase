using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "justification",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    kind = table.Column<string>(type: "text", nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    source_url = table.Column<string>(type: "text", nullable: true),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: true),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_justification", x => x.id);
                    table.ForeignKey(
                        name: "fk_justification_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_justification_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "model_setting",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    record_history = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    require_reason = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_model_setting", x => x.id);
                    table.CheckConstraint("ck_model_setting_one_row", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "change_set",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    justification_id = table.Column<long>(type: "bigint", nullable: false),
                    cause_change_set_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_set", x => x.id);
                    table.ForeignKey(
                        name: "fk_change_set_change_set_cause_change_set_id",
                        column: x => x.cause_change_set_id,
                        principalTable: "change_set",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_change_set_justification_justification_id",
                        column: x => x.justification_id,
                        principalTable: "justification",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "change",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    change_set_id = table.Column<long>(type: "bigint", nullable: false),
                    entity_kind = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    op = table.Column<string>(type: "text", nullable: false),
                    field_key = table.Column<string>(type: "text", nullable: true),
                    rel_type = table.Column<string>(type: "text", nullable: true),
                    related_id = table.Column<string>(type: "text", nullable: true),
                    old_value = table.Column<string>(type: "text", nullable: true),
                    new_value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change", x => x.id);
                    table.ForeignKey(
                        name: "fk_change_change_set_change_set_id",
                        column: x => x.change_set_id,
                        principalTable: "change_set",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_change_change_set_id",
                table: "change",
                column: "change_set_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_entity_kind_entity_id_id",
                table: "change",
                columns: new[] { "entity_kind", "entity_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_change_related_id_id",
                table: "change",
                columns: new[] { "related_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_change_set_cause_change_set_id",
                table: "change_set",
                column: "cause_change_set_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_set_justification_id",
                table: "change_set",
                column: "justification_id");

            migrationBuilder.CreateIndex(
                name: "ix_justification_import_batch_id",
                table: "justification",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_justification_user_id",
                table: "justification",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change");

            migrationBuilder.DropTable(
                name: "model_setting");

            migrationBuilder.DropTable(
                name: "change_set");

            migrationBuilder.DropTable(
                name: "justification");
        }
    }
}

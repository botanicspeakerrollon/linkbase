using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChangeEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "change_event",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    change_set_id = table.Column<long>(type: "bigint", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: true),
                    cause_change_set_id = table.Column<long>(type: "bigint", nullable: true),
                    entities = table.Column<string>(type: "jsonb", nullable: false),
                    entity_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_event", x => x.id);
                    table.ForeignKey(
                        name: "fk_change_event_change_set_cause_change_set_id",
                        column: x => x.cause_change_set_id,
                        principalTable: "change_set",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_change_event_change_set_change_set_id",
                        column: x => x.change_set_id,
                        principalTable: "change_set",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_change_event_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_change_event_cause_change_set_id",
                table: "change_event",
                column: "cause_change_set_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_event_change_set_id",
                table: "change_event",
                column: "change_set_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_event_import_batch_id",
                table: "change_event",
                column: "import_batch_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_event");
        }
    }
}

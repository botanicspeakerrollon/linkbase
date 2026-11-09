using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStagedItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "staged_item",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: true),
                    props = table.Column<string>(type: "jsonb", nullable: true),
                    rel_type = table.Column<string>(type: "text", nullable: true),
                    source_id = table.Column<string>(type: "text", nullable: true),
                    target_id = table.Column<string>(type: "text", nullable: true),
                    target_type = table.Column<string>(type: "text", nullable: true),
                    included = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staged_item", x => x.id);
                    table.ForeignKey(
                        name: "fk_staged_item_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_staged_item_import_batch_id_kind_id",
                table: "staged_item",
                columns: new[] { "import_batch_id", "kind", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "staged_item");
        }
    }
}

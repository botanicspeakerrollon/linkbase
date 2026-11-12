using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHierarchies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "hierarchy_field",
                table: "node_type",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hierarchy_rel",
                table: "node_type",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "hierarchy_level",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    node_type_name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    is_root = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    under = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_hierarchy_level", x => x.id);
                    table.ForeignKey(
                        name: "fk_hierarchy_level_node_type_node_type_name",
                        column: x => x.node_type_name,
                        principalTable: "node_type",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_hierarchy_level_node_type_name_kind",
                table: "hierarchy_level",
                columns: new[] { "node_type_name", "kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "hierarchy_level");

            migrationBuilder.DropColumn(
                name: "hierarchy_field",
                table: "node_type");

            migrationBuilder.DropColumn(
                name: "hierarchy_rel",
                table: "node_type");
        }
    }
}

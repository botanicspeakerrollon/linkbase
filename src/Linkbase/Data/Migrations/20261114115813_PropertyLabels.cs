using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class PropertyLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "node_property_label",
                columns: table => new
                {
                    node_id = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    label_level = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    label_any = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{*}'"),
                    label_all = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_property_label", x => new { x.node_id, x.key });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "node_property_label");
        }
    }
}

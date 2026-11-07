using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeNameOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_node_type_name_key",
                table: "node",
                columns: new[] { "type", "name", "key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_node_type_name_key",
                table: "node");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModuleOwners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "owner",
                table: "node_type",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "owner",
                table: "field_definition",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "owner",
                table: "node_type");

            migrationBuilder.DropColumn(
                name: "owner",
                table: "field_definition");
        }
    }
}

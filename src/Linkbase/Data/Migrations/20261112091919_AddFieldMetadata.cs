using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "name_template",
                table: "node_type",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_links",
                table: "link_definition",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "link_attribute",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "multiple",
                table: "link_attribute",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "field_definition",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "group_name",
                table: "field_definition",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "multiple",
                table: "field_definition",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "name_template",
                table: "node_type");

            migrationBuilder.DropColumn(
                name: "max_links",
                table: "link_definition");

            migrationBuilder.DropColumn(
                name: "description",
                table: "link_attribute");

            migrationBuilder.DropColumn(
                name: "multiple",
                table: "link_attribute");

            migrationBuilder.DropColumn(
                name: "description",
                table: "field_definition");

            migrationBuilder.DropColumn(
                name: "group_name",
                table: "field_definition");

            migrationBuilder.DropColumn(
                name: "multiple",
                table: "field_definition");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class SecurityLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "label_all",
                table: "node_link",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string[]>(
                name: "label_any",
                table: "node_link",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{*}'");

            migrationBuilder.AddColumn<short>(
                name: "label_level",
                table: "node_link",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<string[]>(
                name: "label_all",
                table: "node",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string[]>(
                name: "label_any",
                table: "node",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{*}'");

            migrationBuilder.AddColumn<short>(
                name: "label_level",
                table: "node",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);

            migrationBuilder.AddColumn<string>(
                name: "security",
                table: "model_setting",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "label_all",
                table: "node_link");

            migrationBuilder.DropColumn(
                name: "label_any",
                table: "node_link");

            migrationBuilder.DropColumn(
                name: "label_level",
                table: "node_link");

            migrationBuilder.DropColumn(
                name: "label_all",
                table: "node");

            migrationBuilder.DropColumn(
                name: "label_any",
                table: "node");

            migrationBuilder.DropColumn(
                name: "label_level",
                table: "node");

            migrationBuilder.DropColumn(
                name: "security",
                table: "model_setting");
        }
    }
}

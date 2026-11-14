using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class FieldLabels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "label_all",
                table: "field_definition",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<string[]>(
                name: "label_any",
                table: "field_definition",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{*}'");

            migrationBuilder.AddColumn<short>(
                name: "label_level",
                table: "field_definition",
                type: "smallint",
                nullable: false,
                defaultValue: (short)0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "label_all",
                table: "field_definition");

            migrationBuilder.DropColumn(
                name: "label_any",
                table: "field_definition");

            migrationBuilder.DropColumn(
                name: "label_level",
                table: "field_definition");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalRefsByNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "entity_type",
                table: "external_ref",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<long>(
                name: "entity_id",
                table: "external_ref",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<string>(
                name: "node_id",
                table: "external_ref",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "node_type",
                table: "external_ref",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_external_ref_node_id",
                table: "external_ref",
                column: "node_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_ref_source_node_type_external_id",
                table: "external_ref",
                columns: new[] { "source", "node_type", "external_id" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_external_ref_target",
                table: "external_ref",
                sql: "(entity_type IS NOT NULL AND entity_id IS NOT NULL AND node_id IS NULL AND node_type IS NULL) OR (entity_type IS NULL AND entity_id IS NULL AND node_id IS NOT NULL AND node_type IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_external_ref_node_id",
                table: "external_ref");

            migrationBuilder.DropIndex(
                name: "ix_external_ref_source_node_type_external_id",
                table: "external_ref");

            migrationBuilder.DropCheckConstraint(
                name: "ck_external_ref_target",
                table: "external_ref");

            migrationBuilder.DropColumn(
                name: "node_id",
                table: "external_ref");

            migrationBuilder.DropColumn(
                name: "node_type",
                table: "external_ref");

            migrationBuilder.AlterColumn<string>(
                name: "entity_type",
                table: "external_ref",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "entity_id",
                table: "external_ref",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);
        }
    }
}

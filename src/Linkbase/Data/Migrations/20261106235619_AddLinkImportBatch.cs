using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkImportBatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "import_batch_id",
                table: "node_link",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_node_link_import_batch_id",
                table: "node_link",
                column: "import_batch_id");

            migrationBuilder.AddForeignKey(
                name: "fk_node_link_import_batch_import_batch_id",
                table: "node_link",
                column: "import_batch_id",
                principalTable: "import_batch",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_node_link_import_batch_import_batch_id",
                table: "node_link");

            migrationBuilder.DropIndex(
                name: "ix_node_link_import_batch_id",
                table: "node_link");

            migrationBuilder.DropColumn(
                name: "import_batch_id",
                table: "node_link");
        }
    }
}

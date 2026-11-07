using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "links",
                table: "link_definition",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Counts what's already loaded. A node's id starts with its type in lower case, so no join to node is needed.
            migrationBuilder.Sql(
                """
                UPDATE link_definition d SET links = c.links
                FROM (
                    SELECT split_part(source_id, ':', 1) AS source, rel_type, split_part(target_id, ':', 1) AS target, count(*) AS links
                    FROM node_link
                    GROUP BY 1, 2, 3
                ) c
                WHERE lower(d.source_type) = c.source AND d.rel_type = c.rel_type AND lower(d.target_type) = c.target
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "links",
                table: "link_definition");
        }
    }
}

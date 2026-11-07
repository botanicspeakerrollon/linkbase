using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGenericStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "node_type",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    color = table.Column<string>(type: "text", nullable: true),
                    icon = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_type", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "field_definition",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    node_type_name = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_field_definition", x => x.id);
                    table.ForeignKey(
                        name: "fk_field_definition_node_type_node_type_name",
                        column: x => x.node_type_name,
                        principalTable: "node_type",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "link_definition",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source_type = table.Column<string>(type: "text", nullable: false),
                    rel_type = table.Column<string>(type: "text", nullable: false),
                    target_type = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_link_definition", x => x.id);
                    table.ForeignKey(
                        name: "fk_link_definition_node_type_source_type",
                        column: x => x.source_type,
                        principalTable: "node_type",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_link_definition_node_type_target_type",
                        column: x => x.target_type,
                        principalTable: "node_type",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "node",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    props = table.Column<string>(type: "jsonb", nullable: false),
                    search_text = table.Column<string>(type: "text", nullable: true, computedColumnSql: "graph_search_text(name, props)", stored: true),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node", x => x.id);
                    table.ForeignKey(
                        name: "fk_node_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_node_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_node_node_type_type",
                        column: x => x.type,
                        principalTable: "node_type",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "node_link",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source_id = table.Column<string>(type: "text", nullable: false),
                    rel_type = table.Column<string>(type: "text", nullable: false),
                    target_id = table.Column<string>(type: "text", nullable: false),
                    props = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_link", x => x.id);
                    table.ForeignKey(
                        name: "fk_node_link_node_source_id",
                        column: x => x.source_id,
                        principalTable: "node",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_node_link_node_target_id",
                        column: x => x.target_id,
                        principalTable: "node",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_field_definition_node_type_name_key",
                table: "field_definition",
                columns: new[] { "node_type_name", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_link_definition_source_type_rel_type_target_type",
                table: "link_definition",
                columns: new[] { "source_type", "rel_type", "target_type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_link_definition_target_type",
                table: "link_definition",
                column: "target_type");

            migrationBuilder.CreateIndex(
                name: "ix_node_import_batch_id",
                table: "node",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_node_name",
                table: "node",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_node_search_text",
                table: "node",
                column: "search_text")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_node_type_key",
                table: "node",
                columns: new[] { "type", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_node_user_id",
                table: "node",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_node_link_source_id",
                table: "node_link",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_node_link_target_id",
                table: "node_link",
                column: "target_id");

            // The graph views now take nodes and links from the generic store as well as from climbing's typed tables.
            migrationBuilder.Sql("""
                DROP VIEW graph_nodes;
                CREATE VIEW graph_nodes AS
                SELECT node_id, user_id, label, name, props,
                       graph_search_text(CASE WHEN label = 'Ascent' THEN '' ELSE name END, props) AS search_text
                FROM graph_nodes_climbing
                UNION ALL
                SELECT id, user_id, type, name, props, search_text FROM node;

                ALTER VIEW graph_edges RENAME TO graph_edges_climbing;
                CREATE VIEW graph_edges AS
                SELECT source_id, target_id, rel_type, props FROM graph_edges_climbing
                UNION ALL
                SELECT source_id, target_id, rel_type, props FROM node_link;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP VIEW graph_edges;
                ALTER VIEW graph_edges_climbing RENAME TO graph_edges;

                DROP VIEW graph_nodes;
                CREATE VIEW graph_nodes AS
                SELECT node_id, user_id, label, name, props,
                       graph_search_text(CASE WHEN label = 'Ascent' THEN '' ELSE name END, props) AS search_text
                FROM graph_nodes_climbing;
                """);
            migrationBuilder.DropTable(
                name: "field_definition");

            migrationBuilder.DropTable(
                name: "link_definition");

            migrationBuilder.DropTable(
                name: "node_link");

            migrationBuilder.DropTable(
                name: "node");

            migrationBuilder.DropTable(
                name: "node_type");
        }
    }
}

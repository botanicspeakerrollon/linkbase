using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            // The generated search_text column on node needs this, so it has to exist before the table.
            migrationBuilder.Sql("""
CREATE FUNCTION graph_search_text(name text, props jsonb) RETURNS text
    LANGUAGE sql IMMUTABLE PARALLEL SAFE
    AS $$
    SELECT coalesce(name, '') || ' ' || coalesce((SELECT string_agg(v, ' ') FROM jsonb_each_text(props) kv(k, v)), '')
$$;
""");

            migrationBuilder.CreateTable(
                name: "app_user",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    display_name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_user", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "data_protection_key",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    friendly_name = table.Column<string>(type: "text", nullable: true),
                    xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_key", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "node_type",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    color = table.Column<string>(type: "text", nullable: true),
                    icon = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    owner = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_type", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "graph_scene",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    nodes = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_graph_scene", x => x.id);
                    table.ForeignKey(
                        name: "fk_graph_scene_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "import_batch",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    source_url = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    raw_payload = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_batch", x => x.id);
                    table.ForeignKey(
                        name: "fk_import_batch_app_user_user_id",
                        column: x => x.user_id,
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_definition",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    node_type_name = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    options = table.Column<List<string>>(type: "text[]", nullable: true),
                    required = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    owner = table.Column<string>(type: "text", nullable: true)
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
                    target_type = table.Column<string>(type: "text", nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    links = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
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
                name: "external_ref",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    node_id = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    node_type = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    source = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: false),
                    url = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_ref", x => x.id);
                    table.ForeignKey(
                        name: "fk_external_ref_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
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
                name: "staged_item",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    key = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: true),
                    props = table.Column<string>(type: "jsonb", nullable: true),
                    rel_type = table.Column<string>(type: "text", nullable: true),
                    source_id = table.Column<string>(type: "text", nullable: true),
                    target_id = table.Column<string>(type: "text", nullable: true),
                    target_type = table.Column<string>(type: "text", nullable: true),
                    included = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staged_item", x => x.id);
                    table.ForeignKey(
                        name: "fk_staged_item_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
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
                    props = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'"),
                    import_batch_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_node_link", x => x.id);
                    table.ForeignKey(
                        name: "fk_node_link_import_batch_import_batch_id",
                        column: x => x.import_batch_id,
                        principalTable: "import_batch",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
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
                name: "ix_external_ref_import_batch_id",
                table: "external_ref",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_ref_node_id",
                table: "external_ref",
                column: "node_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_ref_source_node_type_external_id",
                table: "external_ref",
                columns: new[] { "source", "node_type", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_field_definition_node_type_name_key",
                table: "field_definition",
                columns: new[] { "node_type_name", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_graph_scene_user_id_name",
                table: "graph_scene",
                columns: new[] { "user_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_import_batch_user_id",
                table: "import_batch",
                column: "user_id");

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
                name: "ix_node_type_name_key",
                table: "node",
                columns: new[] { "type", "name", "key" });

            migrationBuilder.CreateIndex(
                name: "ix_node_user_id",
                table: "node",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_node_link_import_batch_id",
                table: "node_link",
                column: "import_batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_node_link_source_id",
                table: "node_link",
                column: "source_id");

            migrationBuilder.CreateIndex(
                name: "ix_node_link_target_id",
                table: "node_link",
                column: "target_id");

            migrationBuilder.CreateIndex(
                name: "ix_staged_item_import_batch_id_kind_id",
                table: "staged_item",
                columns: new[] { "import_batch_id", "kind", "id" });

            // The single user every row belongs to until sign-in exists.
            migrationBuilder.Sql("INSERT INTO app_user (display_name) VALUES ('Me');");

            // The graph and climbing views are SQL the model can't describe, so they are written out here.
            migrationBuilder.Sql("""
CREATE VIEW climbing_crag_country AS
 WITH RECURSIVE chain AS (
         SELECT l.source_id AS crag_id,
            l.target_id AS area_id,
            1 AS depth
           FROM (node_link l
             JOIN node c ON (((c.id = l.source_id) AND (c.type = 'Crag'::text))))
          WHERE (l.rel_type = 'IN'::text)
        UNION ALL
         SELECT chain_1.crag_id,
            l.target_id,
            (chain_1.depth + 1)
           FROM (chain chain_1
             JOIN node_link l ON (((l.source_id = chain_1.area_id) AND (l.rel_type = 'IN'::text))))
          WHERE (chain_1.depth < 50)
        )
 SELECT DISTINCT ON (chain.crag_id) chain.crag_id,
    a.id AS country_id,
    a.name AS country
   FROM (chain
     JOIN node a ON ((a.id = chain.area_id)))
  ORDER BY chain.crag_id, chain.depth DESC;
CREATE VIEW climbing_ascent_facts AS
 SELECT a.id AS ascent_id,
    ((a.props ->> 'date'::text))::date AS date,
    (a.props ->> 'date_precision'::text) AS date_precision,
    (EXTRACT(year FROM ((a.props ->> 'date'::text))::date))::integer AS year,
        CASE
            WHEN ((a.props ->> 'date_precision'::text) <> 'year'::text) THEN (EXTRACT(month FROM ((a.props ->> 'date'::text))::date))::integer
            ELSE NULL::integer
        END AS month,
    ((a.props ->> 'date'::text) || (a.props ->> 'date_precision'::text)) AS day_key,
    tl.target_id AS trip_id,
    rl.target_id AS route_id,
    (r.props ->> 'discipline'::text) AS discipline,
    c.id AS crag_id,
    c.name AS crag_name,
    cc.country_id,
    cc.country,
        CASE
            WHEN ((a.props ->> 'date_precision'::text) = 'year'::text) THEN NULL::text
            ELSE (ARRAY['Winter'::text, 'Winter'::text, 'Spring'::text, 'Spring'::text, 'Spring'::text, 'Summer'::text, 'Summer'::text, 'Summer'::text, 'Autumn'::text, 'Autumn'::text, 'Autumn'::text, 'Winter'::text])[
            CASE
                WHEN (cc.country = ANY (ARRAY['Angola'::text, 'Argentina'::text, 'Australia'::text, 'Bolivia'::text, 'Botswana'::text, 'Brazil'::text, 'Chile'::text, 'Eswatini'::text, 'Fiji'::text, 'Lesotho'::text, 'Madagascar'::text, 'Malawi'::text, 'Mauritius'::text, 'Mozambique'::text, 'Namibia'::text, 'New Zealand'::text, 'Papua New Guinea'::text, 'Paraguay'::text, 'Peru'::text, 'South Africa'::text, 'Tanzania'::text, 'Uruguay'::text, 'Zambia'::text, 'Zimbabwe'::text])) THEN ((((EXTRACT(month FROM ((a.props ->> 'date'::text))::date))::integer + 5) % 12) + 1)
                ELSE (EXTRACT(month FROM ((a.props ->> 'date'::text))::date))::integer
            END]
        END AS season
   FROM ((((((node a
     JOIN node_link rl ON (((rl.source_id = a.id) AND (rl.rel_type = 'OF'::text))))
     JOIN node r ON ((r.id = rl.target_id)))
     JOIN node_link cl ON (((cl.source_id = r.id) AND (cl.rel_type = 'AT'::text))))
     JOIN node c ON ((c.id = cl.target_id)))
     JOIN climbing_crag_country cc ON ((cc.crag_id = c.id)))
     LEFT JOIN node_link tl ON (((tl.source_id = a.id) AND (tl.rel_type = 'ON_TRIP'::text))))
  WHERE (a.type = 'Ascent'::text);
CREATE VIEW climbing_ascent_partner AS
 SELECT l.source_id AS ascent_id,
    l.target_id AS partner_id
   FROM (node_link l
     JOIN node a ON (((a.id = l.source_id) AND (a.type = 'Ascent'::text))))
  WHERE (l.rel_type = 'WITH'::text);
CREATE VIEW climbing_partner AS
 SELECT id,
    name
   FROM node n
  WHERE (type = 'Partner'::text);
CREATE VIEW climbing_trip AS
 SELECT id,
    name,
    ((props ->> 'start_date'::text))::date AS start_date,
    ((props ->> 'end_date'::text))::date AS end_date,
    ((props ->> 'sort_date'::text))::date AS sort_date,
    COALESCE(((props ->> 'abroad'::text))::boolean, false) AS abroad,
    COALESCE(((props ->> 'home'::text))::boolean, false) AS home
   FROM node n
  WHERE (type = 'Trip'::text);
CREATE VIEW graph_edges AS
 SELECT source_id,
    target_id,
    rel_type,
    props
   FROM node_link;
CREATE VIEW graph_nodes AS
 SELECT id AS node_id,
    user_id,
    type AS label,
    name,
    props,
    search_text
   FROM node;
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW IF EXISTS graph_nodes, graph_edges, climbing_trip, climbing_partner, climbing_ascent_partner, climbing_ascent_facts, climbing_crag_country; DROP FUNCTION IF EXISTS graph_search_text(text, jsonb)");

            migrationBuilder.DropTable(
                name: "data_protection_key");

            migrationBuilder.DropTable(
                name: "external_ref");

            migrationBuilder.DropTable(
                name: "field_definition");

            migrationBuilder.DropTable(
                name: "graph_scene");

            migrationBuilder.DropTable(
                name: "link_definition");

            migrationBuilder.DropTable(
                name: "node_link");

            migrationBuilder.DropTable(
                name: "staged_item");

            migrationBuilder.DropTable(
                name: "node");

            migrationBuilder.DropTable(
                name: "import_batch");

            migrationBuilder.DropTable(
                name: "node_type");

            migrationBuilder.DropTable(
                name: "app_user");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGraphViewsAndScenes : Migration
    {
        private const string GraphNodesView = """
            CREATE VIEW graph_nodes AS
            SELECT 'area:' || a.id AS node_id, a.user_id,
                   CASE WHEN a.kind = 'country' THEN 'Country' ELSE 'Area' END AS label,
                   a.name,
                   jsonb_build_object('kind', a.kind) AS props
            FROM area a
            UNION ALL
            SELECT 'crag:' || c.id, c.user_id, 'Crag', c.name,
                   jsonb_build_object('rock_type', c.rock_type, 'lat', c.lat, 'lon', c.lon, 'aliases', c.aliases, 'notes', c.notes)
            FROM crag c
            UNION ALL
            SELECT 'route:' || r.id, r.user_id, 'Route', r.name,
                   jsonb_build_object('grade', r.grade, 'grade_system', r.grade_system, 'discipline', r.discipline,
                                      'length_m', r.length_m, 'pitches', r.pitches, 'description', r.description)
            FROM route r
            UNION ALL
            SELECT 'ascent:' || a.id, a.user_id, 'Ascent', r.name,
                   jsonb_build_object('date', a.date, 'date_precision', a.date_precision, 'style', a.style,
                                      'style_raw', a.style_raw, 'grade', r.grade, 'discipline', r.discipline,
                                      'my_rating', a.my_rating, 'source', a.source, 'notes', a.notes)
            FROM ascent a
            JOIN route r ON r.id = a.route_id
            UNION ALL
            SELECT 'partner:' || p.id, p.user_id, 'Partner', p.name, '{}'::jsonb
            FROM partner p
            UNION ALL
            SELECT 'trip:' || t.id, t.user_id, 'Trip', t.name,
                   jsonb_build_object('start_date', t.start_date, 'end_date', t.end_date, 'notes', t.notes,
                                      'abroad', t.abroad, 'home', t.home, 'approximate', t.approximate, 'manual', t.manual)
            FROM trip t;
            """;

        // VISITED comes from each trip's ascents, because a trip's countries are derived rather than stored.
        private const string GraphEdgesView = """
            CREATE VIEW graph_edges AS
            SELECT 'area:' || a.id AS source_id, 'area:' || a.parent_id AS target_id, 'IN' AS rel_type, '{}'::jsonb AS props
            FROM area a
            WHERE a.parent_id IS NOT NULL
            UNION ALL
            SELECT 'crag:' || c.id, 'area:' || c.area_id, 'IN', '{}'::jsonb
            FROM crag c
            UNION ALL
            SELECT 'route:' || r.id, 'crag:' || r.crag_id, 'AT', '{}'::jsonb
            FROM route r
            UNION ALL
            SELECT 'ascent:' || a.id, 'route:' || a.route_id, 'OF', '{}'::jsonb
            FROM ascent a
            UNION ALL
            SELECT 'ascent:' || ap.ascent_id, 'partner:' || ap.partner_id, 'WITH', '{}'::jsonb
            FROM ascent_partner ap
            UNION ALL
            SELECT 'ascent:' || a.id, 'trip:' || a.trip_id, 'ON_TRIP', jsonb_build_object('inferred', a.trip_inferred)
            FROM ascent a
            WHERE a.trip_id IS NOT NULL
            UNION ALL
            SELECT DISTINCT 'trip:' || f.trip_id, 'area:' || f.country_id, 'VISITED', '{}'::jsonb
            FROM ascent_facts f
            WHERE f.trip_id IS NOT NULL;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateIndex(
                name: "ix_graph_scene_user_id_name",
                table: "graph_scene",
                columns: new[] { "user_id", "name" },
                unique: true);

            migrationBuilder.Sql(GraphNodesView);
            migrationBuilder.Sql(GraphEdgesView);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW graph_edges;");
            migrationBuilder.Sql("DROP VIEW graph_nodes;");

            migrationBuilder.DropTable(
                name: "graph_scene");
        }
    }
}

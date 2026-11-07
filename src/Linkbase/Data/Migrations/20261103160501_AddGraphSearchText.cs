using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGraphSearchText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Everything free text can match. Large tables store this in an indexed column, because scanning their
            // JSON takes too long. See docs/performance.md. The climbing data is small enough to compute it live.
            migrationBuilder.Sql("""
                CREATE FUNCTION graph_search_text(name text, props jsonb) RETURNS text
                LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
                    SELECT coalesce(name, '') || ' ' || coalesce((SELECT string_agg(v, ' ') FROM jsonb_each_text(props) kv(k, v)), '')
                $$;

                ALTER VIEW graph_nodes RENAME TO graph_nodes_climbing;

                -- An ascent's name is its route's, so it matches on its own properties rather than on every route search.
                CREATE VIEW graph_nodes AS
                SELECT node_id, user_id, label, name, props,
                       graph_search_text(CASE WHEN label = 'Ascent' THEN '' ELSE name END, props) AS search_text
                FROM graph_nodes_climbing;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP VIEW graph_nodes;
                ALTER VIEW graph_nodes_climbing RENAME TO graph_nodes;
                DROP FUNCTION graph_search_text(text, jsonb);
                """);
        }
    }
}

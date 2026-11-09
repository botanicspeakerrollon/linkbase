using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class ClimbingInsightsOnGenericStore : Migration
    {
        // The Insights reports read these views, which are the climbing logbook as the generic store holds it (see
        // docs/climbing-to-generic-plan.md). In a database with no Climbing records they are empty. They mirror ascent_facts and
        // crag_country, which read the typed tables and stay until those go.
        private const string CragCountryView = """
            CREATE VIEW climbing_crag_country AS
            WITH RECURSIVE chain AS (
                SELECT l.source_id AS crag_id, l.target_id AS area_id, 1 AS depth
                FROM node_link l
                JOIN node c ON c.id = l.source_id AND c.type = 'Crag'
                WHERE l.rel_type = 'IN'
                UNION ALL
                SELECT chain.crag_id, l.target_id, chain.depth + 1
                FROM chain
                JOIN node_link l ON l.source_id = chain.area_id AND l.rel_type = 'IN'
                WHERE chain.depth < 50
            )
            SELECT DISTINCT ON (chain.crag_id) chain.crag_id, a.id AS country_id, a.name AS country
            FROM chain
            JOIN node a ON a.id = chain.area_id
            ORDER BY chain.crag_id, chain.depth DESC;
            """;

        private const string TripView = """
            CREATE VIEW climbing_trip AS
            SELECT n.id, n.name,
                   (n.props ->> 'start_date')::date AS start_date,
                   (n.props ->> 'end_date')::date AS end_date,
                   (n.props ->> 'sort_date')::date AS sort_date,
                   coalesce((n.props ->> 'abroad')::boolean, false) AS abroad,
                   coalesce((n.props ->> 'home')::boolean, false) AS home
            FROM node n
            WHERE n.type = 'Trip';
            """;

        private const string PartnerViews = """
            CREATE VIEW climbing_partner AS
            SELECT n.id, n.name FROM node n WHERE n.type = 'Partner';

            CREATE VIEW climbing_ascent_partner AS
            SELECT l.source_id AS ascent_id, l.target_id AS partner_id
            FROM node_link l
            JOIN node a ON a.id = l.source_id AND a.type = 'Ascent'
            WHERE l.rel_type = 'WITH';
            """;

        // day_key counts a partial date as one distinct day, and the southern hemisphere's seasons are shifted by six
        // months. Ids are node ids, such as route:12.
        private const string AscentFactsView = """
            CREATE VIEW climbing_ascent_facts AS
            SELECT a.id AS ascent_id,
                   (a.props ->> 'date')::date AS date,
                   a.props ->> 'date_precision' AS date_precision,
                   extract(year FROM (a.props ->> 'date')::date)::int AS year,
                   CASE WHEN a.props ->> 'date_precision' <> 'year' THEN extract(month FROM (a.props ->> 'date')::date)::int END AS month,
                   (a.props ->> 'date') || (a.props ->> 'date_precision') AS day_key,
                   tl.target_id AS trip_id,
                   rl.target_id AS route_id,
                   r.props ->> 'discipline' AS discipline,
                   c.id AS crag_id,
                   c.name AS crag_name,
                   cc.country_id,
                   cc.country,
                   CASE
                       WHEN a.props ->> 'date_precision' = 'year' THEN NULL
                       ELSE (ARRAY['Winter', 'Winter', 'Spring', 'Spring', 'Spring', 'Summer',
                                   'Summer', 'Summer', 'Autumn', 'Autumn', 'Autumn', 'Winter'])[
                           CASE WHEN cc.country IN ('Angola', 'Argentina', 'Australia', 'Bolivia', 'Botswana', 'Brazil', 'Chile', 'Eswatini', 'Fiji', 'Lesotho', 'Madagascar', 'Malawi', 'Mauritius', 'Mozambique', 'Namibia', 'New Zealand', 'Papua New Guinea', 'Paraguay', 'Peru', 'South Africa', 'Tanzania', 'Uruguay', 'Zambia', 'Zimbabwe')
                                THEN (extract(month FROM (a.props ->> 'date')::date)::int + 5) % 12 + 1
                                ELSE extract(month FROM (a.props ->> 'date')::date)::int
                           END]
                   END AS season
            FROM node a
            JOIN node_link rl ON rl.source_id = a.id AND rl.rel_type = 'OF'
            JOIN node r ON r.id = rl.target_id
            JOIN node_link cl ON cl.source_id = r.id AND cl.rel_type = 'AT'
            JOIN node c ON c.id = cl.target_id
            JOIN climbing_crag_country cc ON cc.crag_id = c.id
            LEFT JOIN node_link tl ON tl.source_id = a.id AND tl.rel_type = 'ON_TRIP'
            WHERE a.type = 'Ascent';
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CragCountryView);
            migrationBuilder.Sql(TripView);
            migrationBuilder.Sql(PartnerViews);
            migrationBuilder.Sql(AscentFactsView);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW climbing_ascent_facts;");
            migrationBuilder.Sql("DROP VIEW climbing_ascent_partner;");
            migrationBuilder.Sql("DROP VIEW climbing_partner;");
            migrationBuilder.Sql("DROP VIEW climbing_trip;");
            migrationBuilder.Sql("DROP VIEW climbing_crag_country;");
        }
    }
}

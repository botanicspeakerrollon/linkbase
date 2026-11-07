using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Linkbase.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLogbookViews : Migration
    {
        // Mountain Project crags can sit many levels below their country.
        private const string CragCountryView = """
            CREATE VIEW crag_country AS
            WITH RECURSIVE chain AS (
                SELECT c.id AS crag_id, a.id AS area_id, a.parent_id, a.name
                FROM crag c
                JOIN area a ON a.id = c.area_id
                UNION ALL
                SELECT chain.crag_id, p.id, p.parent_id, p.name
                FROM chain
                JOIN area p ON p.id = chain.parent_id
            )
            SELECT crag_id, area_id AS country_id, name AS country
            FROM chain
            WHERE parent_id IS NULL;
            """;

        // Each ascent with its place and season. day_key counts a partial date as one distinct day, and
        // the southern hemisphere's seasons are shifted by six months.
        private const string AscentFactsView = """
            CREATE VIEW ascent_facts AS
            SELECT a.id AS ascent_id,
                   a.user_id,
                   a.date,
                   a.date_precision,
                   extract(year FROM a.date)::int AS year,
                   CASE WHEN a.date_precision <> 'year' THEN extract(month FROM a.date)::int END AS month,
                   a.date::text || a.date_precision AS day_key,
                   a.trip_id,
                   a.route_id,
                   r.discipline,
                   r.crag_id,
                   c.name AS crag_name,
                   cc.country_id,
                   cc.country,
                   CASE
                       WHEN a.date_precision = 'year' THEN NULL
                       ELSE (ARRAY['Winter', 'Winter', 'Spring', 'Spring', 'Spring', 'Summer',
                                   'Summer', 'Summer', 'Autumn', 'Autumn', 'Autumn', 'Winter'])[
                           CASE WHEN cc.country IN ('Angola', 'Argentina', 'Australia', 'Bolivia', 'Botswana', 'Brazil', 'Chile', 'Eswatini', 'Fiji', 'Lesotho', 'Madagascar', 'Malawi', 'Mauritius', 'Mozambique', 'Namibia', 'New Zealand', 'Papua New Guinea', 'Paraguay', 'Peru', 'South Africa', 'Tanzania', 'Uruguay', 'Zambia', 'Zimbabwe')
                                THEN (extract(month FROM a.date)::int + 5) % 12 + 1
                                ELSE extract(month FROM a.date)::int
                           END]
                   END AS season
            FROM ascent a
            JOIN route r ON r.id = a.route_id
            JOIN crag c ON c.id = r.crag_id
            JOIN crag_country cc ON cc.crag_id = c.id;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(CragCountryView);
            migrationBuilder.Sql(AscentFactsView);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW ascent_facts;");
            migrationBuilder.Sql("DROP VIEW crag_country;");
        }
    }
}

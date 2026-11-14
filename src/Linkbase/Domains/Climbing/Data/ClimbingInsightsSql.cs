namespace Linkbase.Data;

// The climbing shapes Insights reads, as a WITH clause put in front of each query. They used to be database views made by
// a migration, which put one domain's SQL into every model's database and bypassed row security. As a CTE the query runs as
// the viewer and reads node and node_link through their policies. Each name below can be used in a query as if it were a table.
// There are no braces here, because the text is joined onto an interpolated string.
internal static class ClimbingInsightsSql
{
    public const string With = """
        WITH RECURSIVE chain AS (
            SELECT l.source_id AS crag_id, l.target_id AS area_id, 1 AS depth
            FROM node_link l
            JOIN node c ON c.id = l.source_id AND c.type = 'Crag'
            WHERE l.rel_type = 'IN'
            UNION ALL
            SELECT ch.crag_id, l.target_id, ch.depth + 1
            FROM chain ch
            JOIN node_link l ON l.source_id = ch.area_id AND l.rel_type = 'IN'
            WHERE ch.depth < 50
        ),
        -- Each crag and the top-level area above it.
        climbing_crag_country AS (
            SELECT DISTINCT ON (chain.crag_id) chain.crag_id, a.id AS country_id, a.name AS country
            FROM chain
            JOIN node a ON a.id = chain.area_id
            ORDER BY chain.crag_id, chain.depth DESC
        ),
        -- One row per ascent, with its place, trip and season (shifted six months in the southern hemisphere).
        climbing_ascent_facts AS (
            SELECT a.id AS ascent_id,
                   (a.props ->> 'date')::date AS date,
                   a.props ->> 'date_precision' AS date_precision,
                   extract(year FROM (a.props ->> 'date')::date)::int AS year,
                   CASE WHEN a.props ->> 'date_precision' <> 'year'
                        THEN extract(month FROM (a.props ->> 'date')::date)::int END AS month,
                   (a.props ->> 'date') || (a.props ->> 'date_precision') AS day_key,
                   tl.target_id AS trip_id,
                   rl.target_id AS route_id,
                   r.props ->> 'discipline' AS discipline,
                   c.id AS crag_id,
                   c.name AS crag_name,
                   cc.country_id,
                   cc.country,
                   CASE WHEN a.props ->> 'date_precision' = 'year' THEN NULL
                        ELSE (ARRAY['Winter', 'Winter', 'Spring', 'Spring', 'Spring', 'Summer', 'Summer', 'Summer', 'Autumn', 'Autumn', 'Autumn', 'Winter'])[
                            CASE WHEN cc.country = ANY (ARRAY['Angola', 'Argentina', 'Australia', 'Bolivia', 'Botswana', 'Brazil', 'Chile', 'Eswatini', 'Fiji',
                                                              'Lesotho', 'Madagascar', 'Malawi', 'Mauritius', 'Mozambique', 'Namibia', 'New Zealand',
                                                              'Papua New Guinea', 'Paraguay', 'Peru', 'South Africa', 'Tanzania', 'Uruguay', 'Zambia', 'Zimbabwe'])
                                 THEN (extract(month FROM (a.props ->> 'date')::date)::int + 5) % 12 + 1
                                 ELSE extract(month FROM (a.props ->> 'date')::date)::int END]
                   END AS season
            FROM node a
            JOIN node_link rl ON rl.source_id = a.id AND rl.rel_type = 'OF'
            JOIN node r ON r.id = rl.target_id
            JOIN node_link cl ON cl.source_id = r.id AND cl.rel_type = 'AT'
            JOIN node c ON c.id = cl.target_id
            JOIN climbing_crag_country cc ON cc.crag_id = c.id
            LEFT JOIN node_link tl ON tl.source_id = a.id AND tl.rel_type = 'ON_TRIP'
            WHERE a.type = 'Ascent'
        ),
        climbing_ascent_partner AS (
            SELECT l.source_id AS ascent_id, l.target_id AS partner_id
            FROM node_link l
            JOIN node a ON a.id = l.source_id AND a.type = 'Ascent'
            WHERE l.rel_type = 'WITH'
        ),
        climbing_partner AS (
            SELECT id, name FROM node WHERE type = 'Partner'
        ),
        climbing_trip AS (
            SELECT id, name,
                   (props ->> 'start_date')::date AS start_date,
                   (props ->> 'end_date')::date AS end_date,
                   (props ->> 'sort_date')::date AS sort_date,
                   COALESCE((props ->> 'abroad')::boolean, false) AS abroad,
                   COALESCE((props ->> 'home')::boolean, false) AS home
            FROM node
            WHERE type = 'Trip'
        )

        """;
}

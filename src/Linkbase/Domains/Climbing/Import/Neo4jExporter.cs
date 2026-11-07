using System.Text.Json;
using Neo4j.Driver;

namespace Linkbase.Domains.Climbing.Import;

public sealed record Neo4jTripLink(string Name, bool Inferred);

public sealed record Neo4jAscent(
    string Id,
    string Source,
    string? Style,
    string? MpStyle,
    string? MpLeadStyle,
    string? Notes,
    string DateText,
    string ClimbName,
    string CragName,
    string? Grade,
    string? Protection,
    long? LengthFt,
    IReadOnlyList<string> Partners,
    IReadOnlyList<Neo4jTripLink> Trips);

public sealed record Neo4jTrip(string Id, string Name, string? Start, string? End, IReadOnlyList<string> Visited);

public sealed record Neo4jExport(IReadOnlyList<Neo4jAscent> Ascents, IReadOnlyList<Neo4jTrip> Trips)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

// Writes the graph to a file with read-only queries, for the one-time comparison against the import.
public static class Neo4jExporter
{
    public static async Task<int> RunAsync(IConfiguration configuration, string path, TextWriter output)
    {
        var password = configuration["Neo4j:Password"];
        if (string.IsNullOrEmpty(password))
        {
            output.WriteLine("Neo4j:Password is not set. See \"Importing existing data\" in README.md.");
            return 1;
        }

        var uri = configuration["Neo4j:Uri"] ?? "bolt://localhost:7687";
        var user = configuration["Neo4j:User"] ?? "neo4j";
        var database = configuration["Neo4j:Database"] ?? "neo4j";

        await using var driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
        var config = new QueryConfig(RoutingControl.Readers, database);

        var ascents = await driver.ExecutableQuery(
            """
            MATCH (a:Ascent)-[:OF]->(c:Climb)-[:AT]->(crag:Crag)
            RETURN toString(a.id) AS id, a.source AS source, a.style AS style, a.mpStyle AS mpStyle,
                   a.mpLeadStyle AS mpLeadStyle, a.notes AS notes, a.dateText AS dateText,
                   c.name AS climbName, crag.name AS cragName, c.grade AS grade,
                   c.protection AS protection, c.lengthFt AS lengthFt,
                   COLLECT { MATCH (a)-[:WITH]->(p:Partner) RETURN p.name } AS partners,
                   COLLECT { MATCH (a)-[r:ON_TRIP]->(t:Trip) RETURN [t.name, coalesce(r.inferred, false)] } AS trips
            """).WithConfig(config).ExecuteAsync();

        var trips = await driver.ExecutableQuery(
            """
            MATCH (t:Trip)
            RETURN t.id AS id, t.name AS name, toString(t.start) AS start, toString(t.end) AS end,
                   COLLECT { MATCH (t)-[:VISITED]->(c:Country) RETURN c.name } AS visited
            """).WithConfig(config).ExecuteAsync();

        var export = new Neo4jExport(
            ascents.Result.Select(r => new Neo4jAscent(
                r["id"].As<string>(),
                r["source"].As<string>(),
                r["style"].As<string?>(),
                r["mpStyle"].As<string?>(),
                r["mpLeadStyle"].As<string?>(),
                r["notes"].As<string?>(),
                r["dateText"].As<string>(),
                r["climbName"].As<string>(),
                r["cragName"].As<string>(),
                r["grade"].As<string?>(),
                r["protection"].As<string?>(),
                r["lengthFt"].As<long?>(),
                r["partners"].As<List<string>>(),
                r["trips"].As<List<List<object>>>().Select(t => new Neo4jTripLink((string)t[0], (bool)t[1])).ToList()))
                .ToList(),
            trips.Result.Select(r => new Neo4jTrip(
                r["id"].As<string>(),
                r["name"].As<string>(),
                r["start"].As<string?>(),
                r["end"].As<string?>(),
                r["visited"].As<List<string>>()))
                .ToList());

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(export, Neo4jExport.JsonOptions));
        output.WriteLine($"Wrote {export.Ascents.Count} ascents and {export.Trips.Count} trips to {path}");
        return 0;
    }
}

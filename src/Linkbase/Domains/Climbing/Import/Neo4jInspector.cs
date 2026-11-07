using Neo4j.Driver;

namespace Linkbase.Domains.Climbing.Import;

// Read-only summary of the existing graph, used to agree a mapping before anything is imported.
// Every query runs with read routing, so the server rejects any accidental write.
public sealed class Neo4jInspector(IDriver driver, string database, TextWriter output)
{
    private const int MaxSampleLength = 60;

    public static async Task<int> RunAsync(IConfiguration configuration, TextWriter output)
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
        try
        {
            await driver.VerifyConnectivityAsync();
        }
        catch (Exception ex) when (ex is Neo4jException or ServiceUnavailableException)
        {
            output.WriteLine($"Could not connect to {uri} as {user}: {ex.Message}");
            return 1;
        }

        await new Neo4jInspector(driver, database, output).PrintSummaryAsync();
        return 0;
    }

    public async Task PrintSummaryAsync()
    {
        output.WriteLine("## Databases");
        foreach (var row in await QueryAsync("SHOW DATABASES YIELD name, currentStatus, default", "system"))
        {
            output.WriteLine($"{row["name"].As<string>()} ({row["currentStatus"].As<string>()}){(row["default"].As<bool>() ? " default" : "")}");
        }

        output.WriteLine();
        output.WriteLine($"Inspecting database: {database}");

        output.WriteLine();
        output.WriteLine("## Nodes by label");
        foreach (var row in await QueryAsync("MATCH (n) RETURN labels(n) AS labels, count(*) AS count ORDER BY count DESC"))
        {
            output.WriteLine($"{Labels(row["labels"])}: {row["count"].As<long>()}");
        }

        output.WriteLine();
        output.WriteLine("## Node properties");
        foreach (var row in await QueryAsync(
            """
            MATCH (n) UNWIND keys(n) AS key
            WITH labels(n) AS labels, key, n[key] AS value
            RETURN labels, key, count(*) AS count,
                   collect(DISTINCT valueType(value))[..3] AS types,
                   collect(DISTINCT value)[..5] AS samples
            ORDER BY labels, key
            """))
        {
            output.WriteLine($"{Labels(row["labels"])}.{row["key"].As<string>()} x{row["count"].As<long>()} {Values(row["types"])}: {Values(row["samples"])}");
        }

        output.WriteLine();
        output.WriteLine("## Relationships");
        foreach (var row in await QueryAsync(
            "MATCH (a)-[r]->(b) RETURN labels(a) AS source, type(r) AS type, labels(b) AS target, count(*) AS count ORDER BY count DESC"))
        {
            output.WriteLine($"{Labels(row["source"])} -[{row["type"].As<string>()}]-> {Labels(row["target"])}: {row["count"].As<long>()}");
        }

        output.WriteLine();
        output.WriteLine("## Relationship properties");
        foreach (var row in await QueryAsync(
            """
            MATCH ()-[r]->() UNWIND keys(r) AS key
            WITH type(r) AS type, key, r[key] AS value
            RETURN type, key, count(*) AS count,
                   collect(DISTINCT valueType(value))[..3] AS types,
                   collect(DISTINCT value)[..5] AS samples
            ORDER BY type, key
            """))
        {
            output.WriteLine($"{row["type"].As<string>()}.{row["key"].As<string>()} x{row["count"].As<long>()} {Values(row["types"])}: {Values(row["samples"])}");
        }

        output.WriteLine();
        output.WriteLine("## Constraints");
        foreach (var row in await QueryAsync("SHOW CONSTRAINTS YIELD name, type, labelsOrTypes, properties"))
        {
            output.WriteLine($"{row["name"].As<string>()}: {row["type"].As<string>()} on {Values(row["labelsOrTypes"])} {Values(row["properties"])}");
        }
    }

    private async Task<IReadOnlyList<IRecord>> QueryAsync(string cypher, string? onDatabase = null)
    {
        var result = await driver.ExecutableQuery(cypher)
            .WithConfig(new QueryConfig(RoutingControl.Readers, onDatabase ?? database))
            .ExecuteAsync();
        return result.Result;
    }

    private static string Labels(object labels) => ":" + string.Join(":", labels.As<List<string>>());

    private static string Values(object values) =>
        "[" + string.Join(", ", values.As<List<object>>().Select(Shorten)) + "]";

    private static string Shorten(object? value)
    {
        var text = (value?.ToString() ?? "null").ReplaceLineEndings(" ");
        return text.Length <= MaxSampleLength ? text : text[..MaxSampleLength] + "...";
    }
}

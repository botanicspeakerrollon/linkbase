using Linkbase.Core.Graph;
using Linkbase.Core.Models;
using Linkbase.Core.Records;

namespace Linkbase.Core.Api;

// The read-only API, version 1: /api/v1/{model}/search and /api/v1/{model}/records/{type}/{key}. Every route needs a caller
// signed in by the scheme in Api:Scheme, and reads as that caller, so row and field security apply as they do on the pages.
// See docs/history/api-plan.md.
public static class ApiEndpoints
{
    public const string Root = "/api/v1";

    public static WebApplication MapLinkbaseApi(this WebApplication app)
    {
        if (!ApiSettings.Enabled(app.Configuration))
        {
            return app;
        }

        var scheme = ApiSettings.Scheme(app.Configuration);
        var api = app.MapGroup($"{Root}/{{model}}")
            .RequireAuthorization(policy => policy.AddAuthenticationSchemes(scheme).RequireAuthenticatedUser())
            .AddEndpointFilter(SelectModelAsync);

        api.MapGet("/search", async (string? q, string[]? type, IGraphRepository graph, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return Error(StatusCodes.Status400BadRequest, "Give the text to look for as q.");
            }

            var nodes = await graph.SearchNodesAsync(q, type is { Length: > 0 } ? type : null, cancellationToken);
            return Results.Json(new
            {
                results = nodes.Where(n => !n.IsModel).Select(n => new
                {
                    id = n.Id,
                    type = n.Label,
                    key = KeyOf(n.Id),
                    name = n.Name,
                    properties = n.Properties.ToDictionary(p => p.Key, p => p.Value),
                }),
            });
        });

        api.MapGet("/records/{type}/{key}", async (string type, string key, IRecordRepository records, CancellationToken cancellationToken) =>
        {
            var record = await records.GetAsync(type, key, cancellationToken);
            if (record is null)
            {
                return Error(StatusCodes.Status404NotFound, "There is no such record.");
            }

            var links = await records.LinksAsync(record.Id, cancellationToken);
            return Results.Json(new
            {
                id = record.Id,
                type = record.Type,
                key = record.Key,
                name = record.Name,
                values = record.Values,
                maskedFields = record.Masked.Order(StringComparer.Ordinal),
                createdAt = record.CreatedAt,
                updatedAt = record.UpdatedAt,
                links = links.Select(g => new
                {
                    rel = g.RelType,
                    direction = g.Outgoing ? "out" : "in",
                    total = g.Total,
                    links = g.Links.Select(l => new { id = l.OtherId, type = l.OtherType, key = l.OtherKey, name = l.OtherName, values = l.Values }),
                }),
            });
        });

        return app;
    }

    private static string KeyOf(string id) => id[(id.IndexOf(':') + 1)..];

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    // Picks the model the route names for this request, so everything after it reads that model's database.
    private static async ValueTask<object?> SelectModelAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var catalog = http.RequestServices.GetRequiredService<ModelCatalog>();
        var key = (string?)http.GetRouteValue("model");
        if (catalog.Find(key) is null)
        {
            // Another instance may have made it since this one last looked.
            await http.RequestServices.GetRequiredService<ModelCreator>().LoadAsync(http.RequestAborted);
        }

        if (catalog.Find(key) is null)
        {
            return Error(StatusCodes.Status404NotFound, "There is no such model.");
        }

        http.RequestServices.GetRequiredService<CurrentModel>().Select(key);
        return await next(context);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Linkbase.Data;

namespace Linkbase.Core.Hooks;

// The simplest kind of hook: one line of JSON per event on standard output, where the log collector picks it up. It stands in
// for a message bus, and shows what any other kind is given.
public sealed class ConsoleHookKind : ChangeHookKind
{
    public override string Id => "console";

    public override IChangeHook Create(string key, IConfigurationSection settings, IServiceProvider services) =>
        new ConsoleChangeHook(key, Console.Out);
}

public sealed class ConsoleChangeHook(string key, TextWriter output) : IChangeHook
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task HandleAsync(IReadOnlyList<ChangeNotice> notices, CancellationToken cancellationToken)
    {
        foreach (var notice in notices)
        {
            await output.WriteLineAsync(Line(key, notice).AsMemory(), cancellationToken);
        }

        await output.FlushAsync(cancellationToken);
    }

    public static string Line(string hook, ChangeNotice notice) => new JsonObject
    {
        ["hook"] = hook,
        ["model"] = notice.Model,
        ["event"] = notice.Id,
        ["time"] = notice.CreatedAt.UtcDateTime,
        ["changeSet"] = notice.ChangeSetId,
        ["kind"] = SnakeCaseEnumConverter<JustificationKind>.ToStore(notice.Kind),
        ["importBatch"] = notice.ImportBatchId,
        ["cause"] = notice.CauseChangeSetId,
        ["count"] = notice.EntityCount,
        ["entities"] = JsonNode.Parse(notice.Entities),
    }.ToJsonString(Options);
}

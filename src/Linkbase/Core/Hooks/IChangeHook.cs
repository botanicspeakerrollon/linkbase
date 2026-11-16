using Linkbase.Data;

namespace Linkbase.Core.Hooks;

// One change event as a hook sees it: the model it happened in and what changed, by id, with no values. Entities is the event's
// JSON as stored. A consumer that wants the values reads them through the API as itself, so row and field security stay in one
// place. See docs/history/change-hooks-plan.md.
public sealed record ChangeNotice(
    string Model, long Id, long? ChangeSetId, JustificationKind Kind, long? ImportBatchId, long? CauseChangeSetId,
    string Entities, int EntityCount, DateTimeOffset CreatedAt);

// Somewhere to tell about changes after they have committed: a log, a webhook, a message bus. A hook can't refuse a change and
// isn't part of its transaction. Events come in commit order, and at least once: throw to be given the same events again, and
// drop repeats by the notice's Id.
public interface IChangeHook
{
    Task HandleAsync(IReadOnlyList<ChangeNotice> notices, CancellationToken cancellationToken);
}

// One kind of hook, as a Hooks:<key>:Kind setting names it. It plays the part for hooks that LlmProvider plays for providers:
// a new kind is one class and one line in ChangeHookKinds.All.
public abstract class ChangeHookKind
{
    // The Kind setting that picks this kind of hook.
    public abstract string Id { get; }

    // Makes the hook that Hooks:<key> describes. Its own settings are in the section.
    public abstract IChangeHook Create(string key, IConfigurationSection settings, IServiceProvider services);
}

// A hook that configuration names, with where and how much it is given.
public sealed record ConfiguredHook(string Key, IReadOnlyList<string> Models, int BatchSize, IChangeHook Hook);

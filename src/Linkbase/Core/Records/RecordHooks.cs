using System.Text.Json.Nodes;
using Linkbase.Core.Models;

namespace Linkbase.Core.Records;

public enum RecordChangeKind { Created, Updated, Deleted, Linked, Unlinked }

// Something that happened to a record. Values are as RecordDetail shows them, empty where there were none: Before for an
// update or a delete, After for a create or an update. A link's change names the relationship and the record at the other
// end, and comes once for each end.
public sealed record RecordChange(
    RecordChangeKind Kind,
    string Type,
    string Id,
    IReadOnlyDictionary<string, string> Before,
    IReadOnlyDictionary<string, string> After,
    string? RelType = null,
    string? OtherId = null);

// A record about to be saved, by the edit form or the CSV import. A hook sets the fields its module keeps, and only those.
public sealed class RecordSaving(string domain, RecordType type, string name, JsonObject props)
{
    public string Type => type.Name;

    public string Name => name;

    // The record's other values, as they'll be stored.
    public JsonNode? Value(string key) => props[key];

    // Null removes the value. A model that doesn't define the field doesn't keep it, so that's ignored. Setting a field
    // the module doesn't own is its mistake, so it throws.
    public void Set(string key, JsonNode? value)
    {
        var field = type.Fields.FirstOrDefault(f => f.Key == key);
        if (field is null)
        {
            return;
        }

        if (field.Owner != domain)
        {
            throw new InvalidOperationException($"{type.Name}'s {key} isn't kept by {domain}, so it can't set it.");
        }

        if (value is null)
        {
            props.Remove(key);
        }
        else
        {
            props[key] = value;
        }
    }
}

// Code a domain module runs when records of its types change. Hooks run only for models that use the module. They don't
// run when a model file is loaded, which replaces everything, or when the module writes its own records.
public interface IRecordHook
{
    // The module's key, such as Climbing.
    string Domain { get; }

    // The types it watches, such as Route and Ascent.
    IReadOnlyCollection<string> Types { get; }

    // Runs before each record of a watched type is saved, in the same transaction, to set the fields the module keeps.
    void BeforeSave(RecordSaving record);

    // Runs once the changes are saved, with every change to a watched type that one action made, such as all the records a
    // CSV import added. It's for keeping derived records up to date, such as trips.
    Task AfterChangeAsync(IReadOnlyList<RecordChange> changes, CancellationToken cancellationToken);
}

// The hooks of the modules the current model uses. With no model given, as in tests, every hook runs.
public sealed class RecordHooks(IEnumerable<IRecordHook> hooks, CurrentModel? model = null)
{
    public static readonly RecordHooks None = new([]);

    private IEnumerable<IRecordHook> Active => model is null ? hooks : hooks.Where(h => model.Info.Domains.Contains(h.Domain));

    public bool Watch(string type) => Active.Any(h => h.Types.Contains(type));

    public IReadOnlyList<string> Watched => Active.SelectMany(h => h.Types).Distinct().ToList();

    public void BeforeSave(RecordType type, string name, JsonObject props)
    {
        foreach (var hook in Active.Where(h => h.Types.Contains(type.Name)))
        {
            hook.BeforeSave(new RecordSaving(hook.Domain, type, name, props));
        }
    }

    public async Task AfterChangeAsync(IReadOnlyList<RecordChange> changes, CancellationToken cancellationToken)
    {
        foreach (var hook in Active)
        {
            var watched = changes.Where(c => hook.Types.Contains(c.Type)).ToList();
            if (watched.Count > 0)
            {
                await hook.AfterChangeAsync(watched, cancellationToken);
            }
        }
    }
}

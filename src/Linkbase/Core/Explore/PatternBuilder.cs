using System.Globalization;
using Linkbase.Data;
using Linkbase.Core.Graph;

namespace Linkbase.Core.Explore;

public enum SuggestionKind { Label, Relationship, Property, Operator, Value, Match, FreeText, Model }

public sealed record PatternSuggestion(SuggestionKind Kind, string Text, string? Hint = null)
{
    public string? Label { get; init; }
    public string? RelType { get; init; }
    public bool Outgoing { get; init; }
    public string? Key { get; init; }
    public FilterOperator? Operator { get; init; }
    public string? Value { get; init; }
}

// One node of the search phrase with its conditions inside it. A null label matches any type of node.
public sealed record PatternChip(string? Label, IReadOnlyList<string> Filters, string? Pending, string? RelationshipBefore);

// The search phrase being built, as in Neo4j Bloom. Each step changes what's suggested next:
// a type, then relationships and properties, a property's operators, then its values. Plain text can go in at any point.
public sealed class PatternBuilder(GraphSchema schema)
{
    private static readonly string[] ModelWords = ["graph", "model", "schema"];

    private readonly List<PatternNodeState> _nodes = [];
    private readonly List<PatternRel> _rels = [];
    private string? _pendingKey;
    private FilterOperator? _pendingOperator;

    private sealed class PatternNodeState
    {
        public string? Label { get; init; }
        public List<PatternFilter> Filters { get; } = [];
    }

    public bool IsEmpty => _nodes.Count == 0;

    // Whether a suggestion needs values from the database for the current step.
    public bool NeedsValues => _pendingOperator is not null;

    public string? CurrentLabel => _nodes.LastOrDefault()?.Label;

    public string? PendingKey => _pendingKey;

    public IReadOnlyList<PatternChip> Chips => _nodes
        .Select((node, i) => new PatternChip(
            node.Label,
            node.Filters.Select(FilterText).ToList(),
            i == _nodes.Count - 1 ? PendingText() : null,
            i > 0 ? RelText(_rels[i - 1].RelType, _rels[i - 1].Outgoing) : null))
        .ToList();

    public GraphPattern? ToPattern() =>
        IsEmpty
            ? null
            : new GraphPattern(_nodes.Select(n => new PatternNode(n.Label, n.Filters.ToList())).ToList(), _rels.ToList());

    public IReadOnlyList<PatternSuggestion> Suggest(string text, IReadOnlyList<PropertyMatch> matches, IReadOnlyList<string> values)
    {
        var term = text.Trim();
        if (_pendingOperator is { } op)
        {
            return SuggestValues(term, op, values);
        }

        if (_pendingKey is not null)
        {
            return OperatorsFor(TypeOf(_pendingKey))
                .Where(o => Matches(OperatorText(o), term))
                .Select(o => new PatternSuggestion(SuggestionKind.Operator, OperatorText(o)) { Operator = o })
                .ToList();
        }

        var suggestions = new List<PatternSuggestion>();
        if (IsEmpty)
        {
            suggestions.AddRange(schema.Labels
                .Where(label => Matches(label, term))
                .Select(label => new PatternSuggestion(SuggestionKind.Label, label, "Type") { Label = label }));

            if (ModelWords.Any(word => Matches(word, term)))
            {
                suggestions.Add(new PatternSuggestion(SuggestionKind.Model, "Graph model", "Every type and how they link"));
            }
        }

        suggestions.AddRange(RelationshipSuggestions(term));

        if (!IsEmpty)
        {
            suggestions.AddRange(schema.Properties
                .Where(p => p.Label == CurrentLabel && Matches(p.Key, term))
                .Select(p => new PatternSuggestion(SuggestionKind.Property, p.Key, "Property") { Key = p.Key }));
        }

        // Always on offer, so anything typed can be searched for even if nothing above matches it.
        if (term.Length > 0)
        {
            suggestions.Add(new PatternSuggestion(SuggestionKind.FreeText, IsEmpty ? $"Search everything for \"{term}\"" : $"{CurrentLabel ?? "Anything"} containing \"{term}\"", "Free text") { Value = term });
        }

        suggestions.AddRange(matches.Select(m =>
            new PatternSuggestion(SuggestionKind.Match, $"{m.Key} = {m.Value}", m.Label) { Label = m.Label, Key = m.Key, Value = m.Value }));
        return suggestions;
    }

    public void Apply(PatternSuggestion suggestion)
    {
        switch (suggestion.Kind)
        {
            case SuggestionKind.Label:
                _nodes.Add(new PatternNodeState { Label = suggestion.Label });
                break;
            case SuggestionKind.Relationship:
                if (IsEmpty)
                {
                    _nodes.Add(new PatternNodeState { Label = null });
                }

                _rels.Add(new PatternRel(suggestion.RelType!, suggestion.Outgoing));
                _nodes.Add(new PatternNodeState { Label = suggestion.Label });
                break;
            case SuggestionKind.Property:
                _pendingKey = suggestion.Key;
                break;
            case SuggestionKind.Operator:
                _pendingOperator = suggestion.Operator;
                break;
            case SuggestionKind.Value:
                _nodes[^1].Filters.Add(new PatternFilter(_pendingKey!, _pendingOperator!.Value, suggestion.Value!));
                _pendingKey = null;
                _pendingOperator = null;
                break;
            case SuggestionKind.FreeText:
                if (IsEmpty)
                {
                    _nodes.Add(new PatternNodeState { Label = null });
                }

                _nodes[^1].Filters.Add(new PatternFilter(PatternFilter.AnyField, FilterOperator.Contains, suggestion.Value!));
                break;
            case SuggestionKind.Match:
                if (IsEmpty)
                {
                    _nodes.Add(new PatternNodeState { Label = suggestion.Label });
                }

                _nodes[^1].Filters.Add(new PatternFilter(suggestion.Key!, FilterOperator.Equals, suggestion.Value!));
                break;
        }
    }

    // Steps back one condition or node, as Backspace does in an empty search box.
    public void RemoveLast()
    {
        if (_pendingOperator is not null)
        {
            _pendingOperator = null;
        }
        else if (_pendingKey is not null)
        {
            _pendingKey = null;
        }
        else if (_nodes.Count > 0)
        {
            var last = _nodes[^1];
            if (last.Filters.Count > 0)
            {
                last.Filters.RemoveAt(last.Filters.Count - 1);
            }
            else
            {
                _nodes.RemoveAt(_nodes.Count - 1);
                if (_rels.Count > 0)
                {
                    _rels.RemoveAt(_rels.Count - 1);
                }

                // A leading "Any" exists only to hold a relationship, so it goes with it.
                if (_nodes is [{ Label: null, Filters.Count: 0 }])
                {
                    _nodes.Clear();
                }
            }
        }
    }

    public void Clear()
    {
        _nodes.Clear();
        _rels.Clear();
        _pendingKey = null;
        _pendingOperator = null;
    }

    public static string RelText(string relType, bool outgoing) => outgoing ? $"{relType} →" : $"← {relType}";

    public static string OperatorText(FilterOperator op) => op switch
    {
        FilterOperator.Equals => "=",
        FilterOperator.Contains => "contains",
        FilterOperator.StartsWith => "starts with",
        FilterOperator.EndsWith => "ends with",
        FilterOperator.GreaterThan => ">",
        FilterOperator.GreaterOrEqual => ">=",
        FilterOperator.LessThan => "<",
        _ => "<=",
    };

    private string? PendingText()
    {
        if (_pendingKey is null)
        {
            return null;
        }

        var key = _pendingKey.Replace('_', ' ');
        return _pendingOperator is { } op ? $"{key} {OperatorText(op)}" : key;
    }

    private static string FilterText(PatternFilter filter) => filter.Key == PatternFilter.AnyField
        ? $"contains \"{filter.Value}\""
        : $"{filter.Key.Replace('_', ' ')} {OperatorText(filter.Operator)} \"{filter.Value}\"";

    private IEnumerable<PatternSuggestion> RelationshipSuggestions(string term)
    {
        // Linking onward from the last node. With nothing chosen yet, typing a relationship's name starts from any node.
        if (IsEmpty && term.Length == 0)
        {
            return [];
        }

        var label = CurrentLabel;
        var anySource = IsEmpty || label is null;

        var outgoing = schema.Connections
            .Where(c => anySource || c.SourceLabel == label)
            .Select(c => (c.RelType, Outgoing: true, Target: c.TargetLabel));
        var incoming = schema.Connections
            .Where(c => anySource || c.TargetLabel == label)
            .Select(c => (c.RelType, Outgoing: false, Target: c.SourceLabel));

        var linked = (IsEmpty ? outgoing : outgoing.Concat(incoming)).Distinct();
        return linked
            .Where(c => Matches(c.RelType, term) || (!IsEmpty && Matches(c.Target, term)))
            .OrderBy(c => c.Target)
            .Select(c => new PatternSuggestion(SuggestionKind.Relationship, $"{RelText(c.RelType, c.Outgoing)} {c.Target}", "Relationship")
            {
                RelType = c.RelType,
                Outgoing = c.Outgoing,
                Label = c.Target,
            });
    }

    private List<PatternSuggestion> SuggestValues(string term, FilterOperator op, IReadOnlyList<string> values)
    {
        var type = TypeOf(_pendingKey!);
        var suggestions = new List<PatternSuggestion>();

        // What's typed is always usable, so a filter isn't limited to values already in the logbook.
        if (term.Length > 0 && IsValid(type, term))
        {
            suggestions.Add(new PatternSuggestion(SuggestionKind.Value, $"{OperatorText(op)} {term}") { Value = term });
        }

        suggestions.AddRange(values
            .Where(v => !string.Equals(v, term, StringComparison.OrdinalIgnoreCase))
            .Select(v => new PatternSuggestion(SuggestionKind.Value, v) { Value = v }));
        return suggestions;
    }

    private GraphValueType TypeOf(string key) =>
        schema.Properties.FirstOrDefault(p => p.Key == key && (CurrentLabel is null || p.Label == CurrentLabel))?.Type ?? GraphValueType.Text;

    public static IEnumerable<FilterOperator> OperatorsFor(GraphValueType type) => type switch
    {
        GraphValueType.Text => [FilterOperator.Contains, FilterOperator.Equals, FilterOperator.StartsWith, FilterOperator.EndsWith],
        GraphValueType.Boolean => [FilterOperator.Equals],
        _ => [FilterOperator.Equals, FilterOperator.GreaterThan, FilterOperator.GreaterOrEqual, FilterOperator.LessThan, FilterOperator.LessOrEqual],
    };

    private static bool IsValid(GraphValueType type, string value) => type switch
    {
        GraphValueType.Number => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
        GraphValueType.Date => DateOnly.TryParse(value, CultureInfo.InvariantCulture, out _),
        GraphValueType.Boolean => value.ToLowerInvariant() is "true" or "false",
        _ => true,
    };

    // Matches from the start of a word, so C offers Crag and Country but not Ascent.
    private static bool Matches(string candidate, string term) =>
        term.Length == 0 || candidate.Split(' ', '_').Any(word => word.StartsWith(term, StringComparison.OrdinalIgnoreCase))
            || candidate.StartsWith(term, StringComparison.OrdinalIgnoreCase);
}

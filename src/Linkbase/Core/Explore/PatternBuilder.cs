using System.Globalization;
using Linkbase.Data;
using Linkbase.Core.Graph;

namespace Linkbase.Core.Explore;

public enum SuggestionKind { Label, Relationship, Property, Operator, Value, Match, FreeText, Model, Rank }

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
// Focused is the node new conditions go to: the last one, unless the arrow keys or a click chose an earlier one.
public sealed record PatternChip(string? Label, IReadOnlyList<string> Filters, string? Pending, string? RelationshipBefore, bool Focused = false);

// The search phrase being built, one step at a time. Each step changes what's suggested next:
// a type, then relationships and properties, a property's operators, then its values. Plain text can go in at any point.
public sealed class PatternBuilder(GraphSchema schema)
{
    private static readonly string[] ModelWords = ["graph", "model", "schema"];

    // Typing one of these, three letters or more, offers a ranking. It isn't listed otherwise, so an ordinary search stays as plain as ever.
    private static readonly string[] RankWords = ["rank", "ranking", "top", "most", "busiest"];
    private static readonly string[] FewestWords = ["fewest", "least", "bottom", "lowest"];

    private readonly List<PatternNodeState> _nodes = [];
    private readonly List<PatternRel> _rels = [];
    private string? _pendingKey;
    private FilterOperator? _pendingOperator;

    // An earlier node chosen to take the next condition. Null means the last one, where the phrase grows.
    private int? _focus;

    private sealed class PatternNodeState
    {
        public string? Label { get; init; }
        public List<PatternFilter> Filters { get; } = [];
    }

    public bool IsEmpty => _nodes.Count == 0;

    // Whether a suggestion needs values from the database for the current step.
    public bool NeedsValues => _pendingOperator is not null;

    private int FocusIndex => _focus ?? _nodes.Count - 1;

    // Whether conditions go to the end of the phrase, where relationships can be added too.
    public bool AtEnd => _focus is null;

    public string? CurrentLabel => IsEmpty ? null : _nodes[FocusIndex].Label;

    public string? PendingKey => _pendingKey;

    // Whether the property being filtered is a list, whose values are suggested one at a time.
    public bool PendingIsList => _pendingKey is not null && TypeOf(_pendingKey) == GraphValueType.List;

    public IReadOnlyList<PatternChip> Chips => _nodes
        .Select((node, i) => new PatternChip(
            node.Label,
            node.Filters.Select(FilterText).ToList(),
            i == FocusIndex ? PendingText() : null,
            i > 0 ? RelText(_rels[i - 1].RelType, _rels[i - 1].Outgoing) : null,
            !AtEnd && i == FocusIndex))
        .ToList();

    public GraphPattern? ToPattern() =>
        IsEmpty
            ? null
            : new GraphPattern(_nodes.Select(n => new PatternNode(n.Label, n.Filters.ToList())).ToList(), _rels.ToList()) { Rank = _rank };

    // A ranking that came from a question, kept while the phrase is edited. It points at steps of the chain, so it goes if
    // either step does.
    private PatternRank? _rank;

    public PatternRank? Rank => _rank;

    // The steps a ranking can point at, named by type. A type that appears twice, or any type, says which step it is.
    public IReadOnlyList<string> StepNames => _nodes
        .Select((node, i) =>
        {
            var name = node.Label ?? "Anything";
            return _nodes.Count(n => n.Label == node.Label) > 1 ? $"{name} (step {i + 1})" : name;
        })
        .ToList();

    public void SetRankTop(int top)
    {
        if (_rank is { } rank)
        {
            _rank = rank with { Top = Math.Clamp(top, 1, PatternRank.MaxTop) };
        }
    }

    // Choosing the step the other end already uses swaps them, so the two are never the same.
    public void SetRankGroup(int step)
    {
        if (_rank is { } rank && step >= 0 && step < _nodes.Count)
        {
            _rank = step == rank.Count ? rank with { Group = step, Count = rank.Group } : rank with { Group = step };
            KeepRankOnlyIfItFits();
        }
    }

    public void SetRankCount(int step)
    {
        if (_rank is { } rank && step >= 0 && step < _nodes.Count)
        {
            _rank = step == rank.Group ? rank with { Count = step, Group = rank.Count } : rank with { Count = step };
            KeepRankOnlyIfItFits();
        }
    }

    public void RemoveRank() => _rank = null;

    // The number fields the counted step could be totalled, averaged or maximised on. Empty when its type has none, or any type.
    public IReadOnlyList<string> NumberKeys(int step) =>
        step >= 0 && step < _nodes.Count && _nodes[step].Label is { } label
            ? schema.Properties.Where(p => p.Label == label && p.Type == GraphValueType.Number).Select(p => p.Key).Distinct().ToList()
            : [];

    // Picking a number measure takes the first number field of the counted type, and does nothing if it has none.
    public void SetRankMeasure(RankMeasure measure)
    {
        if (_rank is not { } rank)
        {
            return;
        }

        if (measure is RankMeasure.Distinct or RankMeasure.Matches)
        {
            _rank = rank with { Measure = measure, Key = null };
        }
        else if (NumberKeys(rank.Count) is { Count: > 0 } keys)
        {
            _rank = rank with { Measure = measure, Key = rank.Key is { } k && keys.Contains(k) ? k : keys[0] };
        }
    }

    public void SetRankKey(string key)
    {
        if (_rank is { NeedsKey: true } rank && NumberKeys(rank.Count).Contains(key))
        {
            _rank = rank with { Key = key };
        }
    }

    public void SetRankFewest(bool fewest)
    {
        if (_rank is { } rank)
        {
            _rank = rank with { Fewest = fewest };
        }
    }

    // After a step is added, removed or swapped the counted type can change. A number field it doesn't have falls back to the
    // first one it does have, or to counting different records.
    private void KeepRankOnlyIfItFits()
    {
        if (_rank is { } rank && (rank.Group >= _nodes.Count || rank.Count >= _nodes.Count))
        {
            _rank = null;
            return;
        }

        if (_rank is { NeedsKey: true } counted && (counted.Key is null || !NumberKeys(counted.Count).Contains(counted.Key)))
        {
            _rank = NumberKeys(counted.Count) is { Count: > 0 } keys
                ? counted with { Key = keys[0] }
                : counted with { Measure = RankMeasure.Distinct, Key = null };
        }
    }

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

        // A relationship links onward from the end, so it's only offered there.
        if (AtEnd)
        {
            suggestions.AddRange(RelationshipSuggestions(term));
        }

        if (!IsEmpty)
        {
            suggestions.AddRange(schema.Properties
                .Where(p => p.Label == CurrentLabel && Matches(p.Key, term))
                .Select(p => new PatternSuggestion(SuggestionKind.Property, p.Key, "Property") { Key = p.Key }));
        }

        // A ranking needs a chain to rank along, and is added once.
        if (term.Length >= 3 && _rank is null && AtEnd && _nodes.Count >= 2 && (RankWords.Any(w => Matches(w, term)) || FewestWords.Any(w => Matches(w, term))))
        {
            var fewest = FewestWords.Any(w => Matches(w, term));
            var rankText = fewest ? "fewest" : "most";
            suggestions.Add(new PatternSuggestion(SuggestionKind.Rank, $"Rank {_nodes[0].Label ?? "Anything"} by {rankText} different {_nodes[^1].Label ?? "Anything"}", "Ranking") { Value = fewest ? "fewest" : null });
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
            case SuggestionKind.Rank:
                // The first step is the one ranked and the last is the one counted, which the chip in the bar can change.
                _rank = new PatternRank(0, _nodes.Count - 1, 5) { Fewest = suggestion.Value == "fewest" };
                break;
            case SuggestionKind.Property:
                _pendingKey = suggestion.Key;
                break;
            case SuggestionKind.Operator:
                _pendingOperator = suggestion.Operator;
                break;
            case SuggestionKind.Value:
                _nodes[FocusIndex].Filters.Add(new PatternFilter(_pendingKey!, _pendingOperator!.Value, suggestion.Value!));
                _pendingKey = null;
                _pendingOperator = null;
                break;
            case SuggestionKind.FreeText:
                if (IsEmpty)
                {
                    _nodes.Add(new PatternNodeState { Label = null });
                }

                _nodes[FocusIndex].Filters.Add(new PatternFilter(PatternFilter.AnyField, FilterOperator.Contains, suggestion.Value!));
                break;
            case SuggestionKind.Match:
                if (IsEmpty)
                {
                    _nodes.Add(new PatternNodeState { Label = suggestion.Label });
                }

                _nodes[FocusIndex].Filters.Add(new PatternFilter(suggestion.Key!, FilterOperator.Equals, suggestion.Value!));
                break;
        }
    }

    // Steps back one condition or node, as Backspace does in an empty search box.
    public void RemoveLast()
    {
        RemoveLastCore();
        KeepRankOnlyIfItFits();
    }

    private void RemoveLastCore()
    {
        if (_pendingOperator is not null)
        {
            _pendingOperator = null;
        }
        else if (_pendingKey is not null)
        {
            _pendingKey = null;
        }
        else if (_focus is { } focus)
        {
            // On an earlier node, Backspace takes off its own conditions. It never removes the node, because the ones after
            // it depend on it.
            var filters = _nodes[focus].Filters;
            if (filters.Count > 0)
            {
                filters.RemoveAt(filters.Count - 1);
            }
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

    // Shows a pattern that came from elsewhere, such as an answer to a question, so it can be read and edited as if typed.
    public void Load(GraphPattern pattern)
    {
        Clear();
        foreach (var node in pattern.Nodes)
        {
            var state = new PatternNodeState { Label = node.Label };
            state.Filters.AddRange(node.Filters);
            _nodes.Add(state);
        }

        _rels.AddRange(pattern.Rels);
        _rank = pattern.Rank;
        KeepRankOnlyIfItFits();
    }

    public void Clear()
    {
        _nodes.Clear();
        _rels.Clear();
        _rank = null;
        _pendingKey = null;
        _pendingOperator = null;
        _focus = null;
    }

    // Chooses which node the next condition goes to. A half-made condition is dropped, since it was for another node.
    public void FocusOn(int index)
    {
        if (index < 0 || index >= _nodes.Count)
        {
            return;
        }

        _focus = index == _nodes.Count - 1 ? null : index;
        _pendingKey = null;
        _pendingOperator = null;
    }

    // The arrow keys, with nothing typed. Returns false at either end, so the key does nothing.
    public bool FocusPrevious()
    {
        if (FocusIndex <= 0 || _pendingKey is not null)
        {
            return false;
        }

        FocusOn(FocusIndex - 1);
        return true;
    }

    public bool FocusNext()
    {
        if (AtEnd || _pendingKey is not null)
        {
            return false;
        }

        FocusOn(FocusIndex + 1);
        return true;
    }

    // Takes one condition off any node, without touching the rest of the phrase.
    public void RemoveFilter(int nodeIndex, int filterIndex)
    {
        if (nodeIndex >= 0 && nodeIndex < _nodes.Count && filterIndex >= 0 && filterIndex < _nodes[nodeIndex].Filters.Count)
        {
            _nodes[nodeIndex].Filters.RemoveAt(filterIndex);
        }
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

        // A choice offers its options, used or not, and nothing else.
        if (type == GraphValueType.Choice)
        {
            return Property(_pendingKey!)!.Options
                .Where(o => Matches(o, term))
                .Select(o => new PatternSuggestion(SuggestionKind.Value, o) { Value = o })
                .ToList();
        }

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

    private GraphValueType TypeOf(string key) => Property(key)?.Type ?? GraphValueType.Text;

    private GraphPropertyInfo? Property(string key) =>
        schema.Properties.FirstOrDefault(p => p.Key == key && (CurrentLabel is null || p.Label == CurrentLabel));

    public static IEnumerable<FilterOperator> OperatorsFor(GraphValueType type) => type switch
    {
        GraphValueType.Text => [FilterOperator.Contains, FilterOperator.Equals, FilterOperator.StartsWith, FilterOperator.EndsWith],
        GraphValueType.Boolean or GraphValueType.Choice => [FilterOperator.Equals],
        GraphValueType.List => [FilterOperator.Contains, FilterOperator.Equals],
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

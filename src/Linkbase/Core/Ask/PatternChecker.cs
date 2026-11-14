using System.Globalization;
using Linkbase.Core.Explore;
using Linkbase.Core.Graph;

namespace Linkbase.Core.Ask;

// Checks a pattern the model wrote against the types, fields and relationships the model really has, and against the
// question it came from. It returns a cleaned copy, or throws QuestionFailedException saying what didn't fit. A pattern that
// passes is still only a pattern: it reaches the database through GraphPatternSql, which allow-lists it again.
public static class PatternChecker
{
    public const int MaxNodes = 5;
    public const int MaxFiltersPerNode = 6;
    public const int MaxValueLength = 100;

    public static GraphPattern Check(GraphPattern pattern, GraphSchema schema, string question)
    {
        if (pattern.Nodes is not { Count: > 0 } nodes || pattern.Rels is null || pattern.Rels.Count != nodes.Count - 1)
        {
            throw new QuestionFailedException("The answer wasn't a complete search. Try asking another way.");
        }

        if (nodes.Count > MaxNodes)
        {
            throw new QuestionFailedException("That question needs a longer chain of types than a question can ask for. Use the search bar.");
        }

        var checkedNodes = new List<PatternNode>();
        foreach (var node in nodes)
        {
            if (node.Label is { } label && !schema.Labels.Contains(label))
            {
                throw new QuestionFailedException($"There's no type called '{label}' in this model.");
            }

            if ((node.Filters?.Count ?? 0) > MaxFiltersPerNode)
            {
                throw new QuestionFailedException("That question has too many conditions. Use the search bar.");
            }

            checkedNodes.Add(new PatternNode(node.Label, (node.Filters ?? []).Select(f => CheckFilter(f, node.Label, schema, question)).ToList()));
        }

        var checkedRels = new List<PatternRel>();
        for (var i = 0; i < pattern.Rels.Count; i++)
        {
            var rel = pattern.Rels[i];
            var (from, to) = (checkedNodes[i].Label, checkedNodes[i + 1].Label);

            // The relationship has to exist between these two types, in the direction given. A null type matches any end.
            var exists = schema.Connections.Any(c => c.RelType == rel.RelType
                && (rel.Outgoing
                    ? (from is null || c.SourceLabel == from) && (to is null || c.TargetLabel == to)
                    : (from is null || c.TargetLabel == from) && (to is null || c.SourceLabel == to)));
            if (!exists)
            {
                throw new QuestionFailedException($"This model has no '{rel.RelType}' link between {from ?? "those"} and {to ?? "those"} records in that direction.");
            }

            if ((rel.Filters?.Count ?? 0) > MaxFiltersPerNode)
            {
                throw new QuestionFailedException("That question has too many conditions. Use the search bar.");
            }

            checkedRels.Add(rel with { Filters = (rel.Filters ?? []).Select(f => CheckLinkFilter(f, rel.RelType, schema, question)).ToList() });
        }

        return new GraphPattern(checkedNodes, checkedRels);
    }

    private static PatternFilter CheckLinkFilter(PatternFilter filter, string relType, GraphSchema schema, string question)
    {
        var value = filter.Value?.Trim() ?? "";
        if (value.Length is 0 or > MaxValueLength)
        {
            throw new QuestionFailedException("A condition in the answer had no usable value.");
        }

        if (filter.Key == PatternFilter.AnyField)
        {
            throw new QuestionFailedException("A link's own condition needs one of its values, not free text.");
        }

        var info = schema.LinkProperties.FirstOrDefault(p => p.RelType == relType && p.Key == filter.Key)
            ?? throw new QuestionFailedException($"'{relType}' has no value called '{filter.Key}'.");
        return CheckAgainst(filter with { Value = value }, info.Type, info.Options, question);
    }

    private static PatternFilter CheckFilter(PatternFilter filter, string? label, GraphSchema schema, string question)
    {
        var value = filter.Value?.Trim() ?? "";
        if (value.Length is 0 or > MaxValueLength)
        {
            throw new QuestionFailedException("A condition in the answer had no usable value.");
        }

        // Free text is any word of the question, so it has to be in the question.
        if (filter.Key == PatternFilter.AnyField)
        {
            RequireInQuestion(value, question);
            return filter with { Value = value, Operator = FilterOperator.Contains };
        }

        var info = schema.Properties.FirstOrDefault(p => p.Key == filter.Key && (label is null || p.Label == label))
            ?? throw new QuestionFailedException($"{(label is null ? "No type" : label)} has no field '{filter.Key}'.");
        return CheckAgainst(filter with { Value = value }, info.Type, info.Options, question);
    }

    // Shared by a node's own filter and a link's: the value matches the field's (or link value's) type, and a choice or
    // free-text value is never invented — it has to be one of the choices, or a word the question itself used.
    private static PatternFilter CheckAgainst(PatternFilter filter, GraphValueType type, IReadOnlyList<string> options, string question)
    {
        var value = filter.Value;
        if (!PatternBuilder.OperatorsFor(type).Contains(filter.Operator))
        {
            throw new QuestionFailedException($"'{PatternBuilder.OperatorText(filter.Operator)}' doesn't work on {filter.Key}.");
        }

        switch (type)
        {
            case GraphValueType.Number when !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _):
                throw new QuestionFailedException($"'{value}' isn't a number for {filter.Key}.");
            case GraphValueType.Date when !DateOnly.TryParse(value, CultureInfo.InvariantCulture, out _):
                throw new QuestionFailedException($"'{value}' isn't a date for {filter.Key}. Dates look like 2024-06-30.");
            case GraphValueType.Boolean when !value.Equals("true", StringComparison.OrdinalIgnoreCase) && !value.Equals("false", StringComparison.OrdinalIgnoreCase):
                throw new QuestionFailedException($"{filter.Key} is true or false.");
            case GraphValueType.Choice:
                value = options.FirstOrDefault(o => o.Equals(value, StringComparison.OrdinalIgnoreCase))
                    ?? throw new QuestionFailedException($"'{value}' isn't one of the choices for {filter.Key}.");
                break;
            case GraphValueType.Text or GraphValueType.List:
                RequireInQuestion(value, question);
                break;
        }

        return filter with { Value = value };
    }

    // The model's wording is never trusted for text: a value it adds that the question didn't say is dropped as invented.
    private static void RequireInQuestion(string value, string question)
    {
        if (!question.Contains(value, StringComparison.OrdinalIgnoreCase))
        {
            throw new QuestionFailedException($"The answer searched for '{value}', which isn't in the question. Try asking another way.");
        }
    }
}

namespace Linkbase.Core.History;

// What a form has collected for the reason box, which fills in as the person types.
public sealed class ReasonDraft
{
    public string? Note { get; set; }
    public string? SourceId { get; set; }
    public string? SourceKind { get; set; }
    public string? SourceComment { get; set; }

    public ChangeReason? ToReason() =>
        new[] { Note, SourceId, SourceKind, SourceComment }.All(string.IsNullOrWhiteSpace)
            ? null
            : new ChangeReason(Note?.Trim() ?? "", SourceId?.Trim(), SourceKind?.Trim(), SourceComment?.Trim());
}

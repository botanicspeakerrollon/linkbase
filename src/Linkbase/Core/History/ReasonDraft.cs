namespace Linkbase.Core.History;

// What a form has collected for the reason box, which fills in as the person types.
public sealed class ReasonDraft
{
    public string? Note { get; set; }
    public string? SourceUrl { get; set; }

    public ChangeReason? ToReason() =>
        string.IsNullOrWhiteSpace(Note) && string.IsNullOrWhiteSpace(SourceUrl) ? null : new ChangeReason(Note?.Trim() ?? "", SourceUrl?.Trim());
}

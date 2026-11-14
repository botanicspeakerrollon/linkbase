namespace Linkbase.Core.Security;

// SQL for readers that must leave out a property by a label a record carries on it (node_property_label), when they filter, sort
// or search in the database and so can't wait to strip values in code. Only for a viewer of a secured model with overrides
// (HiddenFields.HasOverrides): the viewer functions read the connection's own settings, as the row policies do.
public static class PropertyLabelSql
{
    // The keys on the record that this viewer fails, as a text[]. `nodeId` is a SQL expression for the record's id.
    public static string FailingKeys(string nodeId) =>
        $"ARRAY(SELECT l.key FROM node_property_label l WHERE l.node_id = {nodeId} AND NOT (l.label_level <= linkbase_sec.viewer_level() AND l.label_any && linkbase_sec.viewer_any() AND l.label_all <@ linkbase_sec.viewer_all()))";

    // Whether this viewer fails the override on one key (a SQL expression for the key) of the record.
    public static string Fails(string nodeId, string key) =>
        $"EXISTS (SELECT 1 FROM node_property_label l WHERE l.node_id = {nodeId} AND l.key = {key} AND NOT (l.label_level <= linkbase_sec.viewer_level() AND l.label_any && linkbase_sec.viewer_any() AND l.label_all <@ linkbase_sec.viewer_all()))";
}

# Relationship attributes

Values that belong to a link rather than to either record, such as a partner's role on an ascent, or how many days a week a
person works on a project. How they work is in [models](models.md#link-attributes); this is what's built and what isn't.

## Built

First used on the example consultancy's `Person WORKS_ON Project`, which has `role` and `days_a_week`.

- **Definitions.** `link_attribute` (key, type, options, required) belongs to a `link_definition`, and reuses `FieldType` and
  the checks a type's fields get. The values live in `node_link.props`, so Explore's `graph_edges.props` already has them.
- **Model file and loader.** `relationships[].fields`. The loader writes the definitions and the values from the extra
  columns of the relationship's CSV file.
- **Writes.** `RecordRepository.AddLinkAsync` takes values and checks them; `UpdateLinkAsync` edits a link in place. A
  required attribute can't be left empty.
- **Repeats.** The same (source, relationship, target) can be linked again when an attribute's value differs. Identical
  values are a duplicate, so a relationship with no attributes still allows one link between two records. `node_link` has no
  unique index, so this is enforced in `RecordRepository` (`EnsureNotRepeatAsync`), and staged imports only count a link as
  already there when it has no values.
- **Record page.** Each link shows its values as chips, with an edit button, and the Add link dialog has a box for each.
  `FieldInputs` is the shared set of boxes, used by the record form too.

## Not built

- **Import and undo.** The CSV import and Read text make links without values. A staged link has no props of its own yet:
  `staged_item.props` would carry them. Commit and undo would then need the same repeat rule.
- **New record form.** A link made along with a new record can't be given values, so a required attribute doesn't block it.
- **Hooks.** There's no "link changed" event, so a module doesn't hear of an edit to a link's values.
- **Explore.** `graph.js` still only draws; showing a link's values in the tooltip or inspector is left.
- **Filtering** on an attribute in `GraphPattern` (`e{i}.props->>'role'`). Don't index attribute values up front: at Swiss rail
  scale (tens of millions of links) that is a real cost.
- **Derived relationships.** Links to or from a module's derived types are the module's, so people can't edit their values.
  A module doesn't write any yet.
- **Sensitive attributes.** Values in `props` JSON are fine until field-level security needs to protect one; sensitive ones
  would need typed columns (see `security-roadmap.md`).
- **Climbing's first use.** `WITH` (ascent to partner) is the likely candidate, but which attributes are wanted is not
  decided.

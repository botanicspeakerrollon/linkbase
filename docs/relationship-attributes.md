# Relationship attributes (idea, not started)

Values that belong to a link rather than to either record, such as a partner's role on an ascent, or the arrival time of
a service at a stop.

## What exists

`node_link.props` is a `jsonb` column defaulting to `{}`, and `graph_edges` already exposes it. Nothing reads or writes
it yet, so most of the work is definitions, validation and UI.

## What it would take

1. **Definitions.** A `link_attribute` table beside `field_definition`, with the same shape (`key`, `type`, `options`,
   `required`, `owner`) but a foreign key to `link_definition.id`. Reuse `FieldType` and its validation.
2. **Model file and loader.** `relationships[].fields`, mirroring a type's `fields`. `ModelLoader` validates the values and
   writes them to `node_link.props`. The CSV link source needs extra columns beyond the two key columns; this is the
   biggest loader change.
3. **Writes.** `RecordRepository.Writes` adds and removes links by endpoints and relationship. It would also take and
   validate attribute values, and gain an edit-in-place operation. A required attribute blocks the save, as a required
   field does. Domain hooks (`ClimbingRecordHook`) would need a "link changed" event if modules should see edits.
4. **Import and undo.** `staged_item.props` is documented as the record's props; a staged link would reuse it or get its
   own column. Commit and undo in `Core/Staging` carry the values through, and need a rule for duplicates (below).
5. **UI.** The record page shows each link's attribute values, and the add/edit link form gets inputs for them, with
   keyboard handling. Explore passes `props` through to the edge tooltip or inspector; `graph.js` still only draws.
6. **Queries.** `graph_edges.props` already comes from the column, so the rule against `to_jsonb(row)` holds. Filtering on
   an attribute in `GraphPattern` (`e{i}.props->>'role'`) is optional and can come later. Don't index attribute values up
   front: at Swiss rail scale (tens of millions of links) that is a real cost.

Rough size: five commits (entity and migration, loader and model format, repository writes and validation, record page
UI, import/staging/undo), with Explore display and attribute filtering as extras. One migration, for `link_attribute`.

## Open decisions

- **Duplicate links.** A repeat of the same (source, relationship, target) is a duplicate today. With attributes it might
  be legitimate, such as a service calling at a stop twice with different times. Either keep one link per triple, with
  attributes describing it, or allow repeats, which changes uniqueness, import and undo.
- **Derived relationships.** Links to or from types owned by a domain module belong to the module, so their attributes
  should be read-only for people.
- **Sensitive attributes.** Values in `props` JSON are fine until field-level security needs to protect one; sensitive ones
  would need typed columns (see `security-roadmap.md`).
- **Climbing's first use.** `WITH` (ascent to partner) is the likely candidate, but which attributes are wanted is not
  decided. That answer says whether this is needed now.

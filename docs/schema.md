# Database schema

The tables, views and indexes in every model's database, as created by the `InitialCreate` baseline migration
(`src/Linkbase/Data/Migrations`). Names are snake_case. Enums are stored as snake_case text. No foreign key cascades:
deleting something that is still referenced fails. All ids are `bigint` identity columns except `node.id`, which is text.

Extension: `pg_trgm` (for the trigram search indexes).

## Tables

### `app_user`
The single user every row belongs to until sign-in exists. The migration inserts one row, `Me`.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | bigint | PK, identity |
| `display_name` | text | |

### `node_type`
A kind of thing in the model (Crag, Route, Ascent...).

| Column | Type | Notes |
| --- | --- | --- |
| `name` | text | PK |
| `color` | text | nullable |
| `icon` | text | nullable |
| `sort_order` | integer | |
| `owner` | text | nullable; the domain module that owns it |
| `hierarchy_field` | text | nullable; set when the type's records form a tree: the choice field saying which level a record is on |
| `hierarchy_rel` | text | nullable; the relationship from a record to its parent |
| `name_template` | text | nullable; makes a record's name from its fields, such as `{name_first} {name_last}` |

### `hierarchy_level`
One level of a type's hierarchy, such as `region`. See [hierarchies](models.md#hierarchies).

| Column | Type | Notes |
| --- | --- | --- |
| `id` | bigint | PK, identity; the order the model file gave |
| `node_type_name` | text | FK to `node_type.name` |
| `kind` | text | one of the options of the type's `hierarchy_field` |
| `is_root` | boolean | default false; records at this level have no parent |
| `under` | text[] | the kinds a record at this level may be in; empty for a root |

Unique: (`node_type_name`, `kind`).

### `field_definition`
A property a node type has. Values live in `node.props`.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | bigint | PK, identity |
| `node_type_name` | text | FK to `node_type.name` |
| `key` | text | |
| `type` | text | field type, an enum |
| `options` | text[] | nullable; the allowed values for a choice field |
| `required` | boolean | default false |
| `owner` | text | nullable |
| `description` | text | nullable; shown to people beside the field |
| `group_name` | text | nullable; fields with the same group are shown together under it |
| `multiple` | boolean | default false; the field holds several values of its type, as a JSON array |

Unique: (`node_type_name`, `key`).


### `link_definition`
A relationship the model allows: source type, relationship, target type.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | bigint | PK, identity |
| `source_type` | text | FK to `node_type.name` |
| `rel_type` | text | |
| `target_type` | text | FK to `node_type.name` |
| `required` | boolean | default false |
| `max_links` | integer | nullable; the most links of this kind one source record may have |
| `links` | bigint
 | default 0; count of `node_link` rows for this relationship, kept by the code that adds or removes links |

Unique: (`source_type`, `rel_type`, `target_type`).

### `node`
Every record in the model.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | text | PK |
| `user_id` | bigint | FK to `app_user.id` |
| `type` | text | FK to `node_type.name` |
| `key` | text | natural key, unique within a type |
| `name` | text | |
| `props` | jsonb | the record's field values |
| `search_text` | text | generated, stored: `graph_search_text(name, props)` |
| `import_batch_id` | bigint | nullable; FK to `import_batch.id` |
| `created_at` | timestamptz | default `now()` |
| `updated_at` | timestamptz | default `now()`; equals `created_at` until edited |
| `label_level` | smallint | default 0; position in the model's level list, 0 lowest |
| `label_any` | text[] | default `{*}`; the any-of set, `*` meaning unrestricted |
| `label_all` | text[] | default empty; the all-of set |

### `node_link`
A directed relationship between two nodes.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | bigint | PK, identity |
| `source_id` | text | FK to `node.id` |
| `rel_type` | text | |
| `target_id` | text | FK to `node.id` |
| `props` | jsonb | default `{}` |
| `import_batch_id` | bigint | nullable; FK to `import_batch.id` |
| `label_level` | smallint | default 0; position in the model's level list, 0 lowest |
| `label_any` | text[] | default `{*}`; the any-of set, `*` meaning unrestricted |
| `label_all` | text[] | default empty; the all-of set |

### `external_ref`
Where a node came from in another system. It points at a node by `node_id` but has no foreign key, so code that
deletes a node must delete its refs in the same transaction.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | bigint | PK, identity |
| `node_id` | text | default empty; no FK |
| `node_type` | text | default empty |
| `source` | text | |
| `external_id` | text | |
| `url` | text | nullable |
| `created_at` | timestamptz | default `now()` |
| `updated_at` | timestamptz | default `now()` |
| `import_batch_id` | bigint | nullable; FK to `import_batch.id` |

Unique: (`source`, `node_type`, `external_id`).

### `import_batch`
One import run. Undo removes the rows that carry its id.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | bigint | PK, identity |
| `user_id` | bigint | FK to `app_user.id` |
| `source` | text | |
| `source_url` | text | nullable |
| `status` | text | an enum |
| `raw_payload` | jsonb | nullable; never the page's own text |
| `created_at` | timestamptz | default `now()` |
| `updated_at` | timestamptz | default `now()` |

### `staged_item`
A node or link proposed by an import, waiting for review.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | bigint | PK, identity |
| `import_batch_id` | bigint | FK to `import_batch.id` |
| `kind` | text | node or link |
| `type` | text | |
| `key` | text | nullable |
| `name` | text | nullable |
| `props` | jsonb | nullable |
| `rel_type` | text | nullable; links only |
| `source_id` | text | nullable; links only |
| `target_id` | text | nullable; links only |
| `target_type` | text | nullable; links only |
| `included` | boolean | whether the reviewer kept it |

### `saved_view`
A named state of Explore or of one type's record list. `state` is the text the page's address carries in its `v` parameter. An
Explore view saved with its layout pinned has each node's canvas position in `state` too; reopening it places nodes back
exactly where they were instead of laying them out fresh.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | bigint | PK, identity |
| `user_id` | bigint | FK to `app_user.id` |
| `name` | text | |
| `kind` | text | `explore` or `list` |
| `scope` | text | nullable; the type a list view is of |
| `state` | text | URL-safe base64 of JSON; see `Core/Views/ViewState.cs` |
| `pinned` | boolean | shown on Home |
| `created_at` | timestamptz | |
| `updated_at` | timestamptz | |

Unique: (`user_id`, `name`).

### `data_protection_key`
ASP.NET Core data protection keys.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | integer | PK, identity |
| `friendly_name` | text | nullable |
| `xml` | text | nullable |

### `model_setting`
The model's history settings, from its model file. At most one row (`id = 1`); none means both are on.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | integer | PK, always 1 |
| `record_history` | boolean | default `true`. Whether changes are logged |
| `require_reason` | boolean | default `true`. Whether a person has to give a reason |
| `source_kinds` | text[] | default empty. The kinds of source a change can name; empty means just `Internet` |
| `security` | jsonb | nullable. The model's `security` section, null when it uses none |

### `justification`
The why of a change set. `note` is a brief paraphrase, never text copied from a source.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | bigint | PK, identity |
| `kind` | text | `manual`, `import`, `derived` or `correction` |
| `note` | text | nullable |
| `source_id` | text | nullable. What identifies the source: a web address, a ticket number, a document name |
| `source_kind` | text | nullable. One of the model's `source_kinds` |
| `source_comment` | text | nullable. A brief paraphrased remark about the source |
| `import_batch_id` | bigint | nullable, FK to `import_batch.id` |
| `user_id` | bigint | FK to `app_user.id` |
| `created_at` | timestamptz | default `now()` |

### `change_set`
One intent saved together: one edit, one import commit, one rebuild by a module.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | bigint | PK, identity |
| `justification_id` | bigint | FK to `justification.id` |
| `cause_change_set_id` | bigint | nullable, FK to `change_set.id`. The change that made a module rebuild its records |
| `created_at` | timestamptz | default `now()` |

### `change`
One thing that changed. An update has a row for each field or link attribute that changed (`field_key`, with `old_value`
and `new_value` as text). A create or delete of a record has one row whose value is JSON (`{"name": ..., "values": {...}}`),
and a link's is its values as JSON. `entity_id` and `related_id` have no foreign keys: a model reload replaces the records,
and the history of what was there stays.

| Column | Type | Notes |
| --- | --- | --- |
| `id` | bigint | PK, identity |
| `change_set_id` | bigint | FK to `change_set.id` |
| `entity_kind` | text | `node`, `link`, `external_ref`, `definition` or `import_batch` |
| `entity_id` | text | the record's id; for a link its source; for an `import_batch` the batch id; for a `definition` such as `type:Supplier` |
| `op` | text | `create`, `update` or `delete` |
| `field_key` | text | nullable. `name` or a field's key; for an `external_ref` its source |
| `rel_type` | text | nullable. A link's relationship |
| `related_id` | text | nullable. A link's target |
| `old_value`, `new_value` | text | nullable |

Indexes: (`entity_kind`, `entity_id`, `id`), (`related_id`, `id`), `change_set_id`. Created by `AddAuditHistory`.

## Relationships

```
app_user ──< node, import_batch, saved_view
node_type ──< node, field_definition, link_definition (as source and as target)
node ──< node_link (as source and as target)
import_batch ──< node, node_link, external_ref, staged_item, justification
justification ──< change_set ──< change
```

## Indexes (besides primary keys)

| Table | Index | Columns | Notes |
| --- | --- | --- | --- |
| `node` | `ix_node_type_key` | `type`, `key` | unique |
| `node` | `ix_node_type_name_key` | `type`, `name`, `key` | for name-ordered lists |
| `node` | `ix_node_name` | `name` | GIN, `gin_trgm_ops` |
| `node` | `ix_node_search_text` | `search_text` | GIN, `gin_trgm_ops` |
| `node` | `ix_node_user_id` | `user_id` | |
| `node` | `ix_node_import_batch_id` | `import_batch_id` | |
| `node_link` | `ix_node_link_source_id` | `source_id` | |
| `node_link` | `ix_node_link_target_id` | `target_id` | |
| `node_link` | `ix_node_link_import_batch_id` | `import_batch_id` | |
| `external_ref` | `ix_external_ref_source_node_type_external_id` | `source`, `node_type`, `external_id` | unique |
| `external_ref` | `ix_external_ref_node_id` | `node_id` | |
| `external_ref` | `ix_external_ref_import_batch_id` | `import_batch_id` | |
| `field_definition` | `ix_field_definition_node_type_name_key` | `node_type_name`, `key` | unique |
| `link_definition` | `ix_link_definition_source_type_rel_type_target_type` | `source_type`, `rel_type`, `target_type` | unique |
| `link_definition` | `ix_link_definition_target_type` | `target_type` | |
| `import_batch` | `ix_import_batch_user_id` | `user_id` | |
| `staged_item` | `ix_staged_item_import_batch_id_kind_id` | `import_batch_id`, `kind`, `id` | |

## Function

`graph_search_text(name text, props jsonb) returns text` is immutable. It joins the name and every top-level value in
`props` with spaces. It feeds the generated `node.search_text` column, so it is created before the `node` table.

## Views

Written as raw SQL in the migration, because the EF model can't describe them.

| View | What it gives |
| --- | --- |
| `graph_nodes` | `node_id`, `user_id`, `label` (the node type), `name`, `props`, `search_text`, from `node` |
| `graph_edges` | `source_id`, `target_id`, `rel_type`, `props`, from `node_link` |

Climbing's Insights shapes (`climbing_ascent_facts`, `climbing_trip` and the others) are not views. They are a `WITH` clause in
`ClimbingInsightsSql`, put in front of each Insights query, so no model's database carries climbing SQL and the queries read
`node` and `node_link` as the viewer, through their row policies. `InitialCreate` still creates the old `climbing_*` views and
`DropClimbingViews` drops them.

## Migration history

`__EFMigrationsHistory` has `20261113025612_InitialCreate`, the one baseline, then any later migrations in
`src/Linkbase/Data/Migrations`.

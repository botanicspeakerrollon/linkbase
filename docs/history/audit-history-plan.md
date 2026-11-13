# Audit history plan

Built, apart from the two things listed under [not done](#not-done). This adds an audit trail: what changed, who changed it,
when, and why. Explore's **As of** control reads it to show the graph on a past day (see [reference](reference.md#as-of)), but
nothing queries it as a temporal table. How to use it is in [models](models.md#history).

## Decisions

- **History is on for every model.** A model file can opt out.
- **A reason is required for every change.** A model file can make it optional.
- **Append-only log, written by the repository.** No temporal tables, no triggers, no event sourcing. Current-state
  queries and the graph views stay as they are.
- **Audit trail first.** The log is the only record of the past. Explore's As of control rewinds values from it, and nothing else does.

## Model file settings

```json
{
  "name": "Swiss rail",
  "history": { "record": false }
}
```

```json
{
  "name": "Projects",
  "history": { "requireReason": false }
}
```

| Setting | Default | Meaning |
| --- | --- | --- |
| `history.record` | `true` | Keep a change log. `false` keeps nothing, and then `requireReason` means nothing. |
| `history.requireReason` | `true` | A change can't be saved without a reason. `false` makes the reason box optional. |
| `history.sourceKinds` | `["Internet"]` | The kinds of source a change can name, such as `Internet` or the applications of the organisation. |

`ModelFile.Validate` rejects `record: false` together with `requireReason: true` written explicitly. Both settings are saved
with the model in a one-row `model_setting` table, so the running app knows them without the file. Turning history off
later keeps the log already written but stops adding to it.

## Tables

| Table | Holds |
| --- | --- |
| `justification` | The why: `kind` (`manual`, `import`, `derived`, `correction`), a short paraphrased `note`, a source (`source_id`, `source_kind`, `source_comment`), optional `import_batch_id`, `user_id`, `created_at`. |
| `change_set` | One intent or transaction: one save, one import commit, one trip rebuild. Has one `justification_id`, and `cause_change_set_id` for derived work. |
| `change` | One row per thing changed: `change_set_id`, `entity_kind` (`node`, `link`, `external_ref`, `definition`), `entity_id`, `op` (`create`, `update`, `delete`), `field_key`, `old_value`, `new_value`. |

- An update writes one `change` row per changed field. Link attributes use their key in `node_link.props`.
- A delete writes the whole old record (props and links), so it can be understood and restored.
- Values are text or JSON, like `props`.
- Foreign keys never cascade. Snake_case names and enum converters follow the existing conventions.
- Facts only: a `note` is a brief paraphrase, never pasted source text.

## Writing the log

- `RecordRepository` writes the change set in the same transaction as the data, through a `ChangeRecorder`, so the two
  can't disagree.
- `ExecuteUpdate` and `ExecuteDelete` skip `SaveChanges`, so each must call the recorder. A test fails if a write path doesn't.
- **Import creates are not logged row by row.** A create is implied by `import_batch_id`, and the batch's justification
  carries the source. Updates and deletes of existing records are logged. Without this, a 45M-link import would write 45M
  history rows.
- **Derived records** (trips from `ClimbingRecordHook`) log one `derived` change set pointing at the change that caused it.
- **Model structure changes** (types, fields, relationships, hierarchy rules, `model migrate`) log as
  `entity_kind = definition`, so old values can still be read later.
- Where a reason is required, the service throws `LinkbaseValidationException` if it is missing. Imports and corrections
  supply theirs (the import's source, the corrections file), so only hand edits ask.

## UI

- `RecordDialog` gets a Reason box and an optional source URL. It is required unless the model says otherwise, reachable
  by keyboard, and focused first when the dialog opens on an edit.
- A History timeline on the record page and in Explore's link details: who, when, what changed, why, the source.
- Import review needs no extra typing.

## Interaction with other work

- **Undo.** "Edited since the import" becomes "has a change after the batch committed", replacing the `updated_at`
  heuristic. Reverting one change set becomes possible.
- **Field-level security.** History keeps old values, so it can leak fields hidden elsewhere. Every change row carries its
  field key so the same rules can redact it. Add this to [security-roadmap](security-roadmap.md) when permissions are
  designed.

## Steps

1. Migration, `ChangeRecorder`, the model file settings, and logging of manual edits and deletes of records and links. **Done**
2. Staging commit and undo, module rebuilds, definition changes, and the Reason box. **Done**
3. History timeline on the record page. **Done**

## Reverting

`ChangeReverter` puts back what one manual edit changed. The History list on a record page has a **Revert** button on each edit.
A revert is an edit of its own: it goes through the repository, is logged as "Reverted change #n" and runs the hooks. It goes
ahead only when things are still as that edit left them. A value changed again since, a link that has gone or a record that has
been edited stops it, with a message, and nothing is changed.

It reverts a field edit, a link added, removed or edited, a record created (if nothing has been done to it) and a record deleted
(back with its values and the links its type requires; its ids in other systems aren't restored). A change that touched several
records, an import, a correction and a derived change can't be reverted this way: undo an import from the imports page, and
revert the change that caused a derived one. Updates made by an import are put back when that import is undone, using the same
old values. A deleted record has no page to show its History on, so reverting a delete has no button yet.

## Not done

(History in Explore's link details is done: click a link and its history appears under its values.)

- **Reverting more than one record's change, or a deletion from a record page that no longer exists.** See [reverting](#reverting) for what works.
- **The precise undo rule.** Import undo still decides a record was "edited since" from `updated_at`. A link whose values were
  edited since isn't noticed either, because links have no timestamps.

## Notes on what was built

- `ChangeRecorder` is the one place the log is written, and writes through raw SQL on the context's own connection and
  transaction, so the record page's repositories, the staging commit (which uses a bare connection) and the model loader all
  share it. `ChangeRecorderCoverageTests` reads the source and fails when a file writes `node` or `node_link` without it.
- The one write it doesn't cover is climbing's renaming of ascents after their route (`ClimbingRecordHook`), which follows
  from the route's logged rename.
- A domain module's rebuild finds its cause through an `AsyncLocal`: the repository sets the change set while the module's
  hooks run, and `ReplaceDerivedAsync` reads it into `cause_change_set_id`.
- A module's links are not listed in a rebuild, only its records. The links follow from the records.

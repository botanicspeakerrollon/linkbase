# Linkbase Reference

What is in Linkbase, feature by feature. This is not a tutorial (the in-app guide, under Guide in the rail, is that) and it is
not a design discussion (the plans in [docs/](README.md) are that). It says what each part does, what it takes, what it
won't do, and where its edges are. If this page and the app disagree, the app is right and this page is a bug.

## How to read it

Each feature in [Part II](#part-ii-the-features) is a reference page in the manner of a manual:

| Heading | Says |
| --- | --- |
| **Synopsis** | Where you find it and how you start it |
| **Description** | What it does, in the order you'd meet it |
| **Limits** | The numbers and rules that stop it, and what you see when they do |
| **Not yet** | What a reader might expect and isn't built. Nothing here is promised |
| **See also** | Related pages, and the code that does it |

A feature carries one of three marks: **Built**, **Partial** (works, with a stated gap) or **Not built**.
[Appendix B](#appendix-b-status-at-a-glance) lists them all on one page.

```
                  ┌────────────────────────────── one ASP.NET Core app ──────────────────────────────┐
  you ── browser ─┤  Explore · Records · Import · Home · Timeline · Cleanup · Quick-find · Guide      │
                  │        │             │            │                                              │
                  │   Graph queries   RecordRepository   ImportStagingService      ChangeRecorder     │
                  │        └─────────────┴────────────┴──────────────┬──────────────┘                 │
                  └───────────────────────────────────────────────────┼────────────────────────────────┘
                                                                      │ one database per model
                                                       Postgres: node · node_link · history · …
                                                       Model provider (Anthropic or OpenAI-compatible): only for Read text and Ask
```

---

# Part I. Concepts

## 1. The data model

Everything is a **record** (a row in `node`) or a **link** (a row in `node_link`).

- A record has a **type**, a **key** that is unique within its type, a **name**, and **fields**. Its id is the type in lower case,
  a colon and the key, such as `stop:8503000`, so ids survive a reload.
- A **field** has a type: `text`, `number`, `date`, `boolean`, `choice` (one of up to 50 listed values) or `list`
  (several texts). A field may be `required`, may hold `multiple` values, and may carry a description and a group.
- A link joins two records and has a **relationship** name such as `WORKS_ON`. The model says which types a relationship may join,
  which way it points, whether every record of the source type needs one (`required`), and how many one record may have (`max`).
- A link may carry **values of its own** (a person's `role` on a project). Two records may then be linked the same way twice, if
  the values differ.
- Values live in a `props` JSON column; names are indexed for search and sorting.

## 2. Models

A **model** is one self-contained set of types, records and links, in a database of its own. Models never mix and have no
links between them. The rail's switcher moves between them; one model alone hides the switcher.

| Kind | Made by | Has |
| --- | --- | --- |
| **Generic model** | A model file, or **Start from CSVs** in the app | Every page in Part II except the climbing ones |
| **Domain model** | A model file plus a domain module in code | The above, plus the module's pages, importers and derived data. Climbing is the only one |

## 3. Three rules that run through everything

1. **History is automatic.** Anything that adds, changes or removes a record or link writes the audit trail in the same
   transaction ([chapter 11](#11-history-and-revert)). A failed save leaves neither the change nor its history.
2. **Nothing the AI says is trusted.** Read text and Ask both treat the model's answer as untrusted data: every name and value
   is checked against the source or the schema before it is used, and the result is a pattern or a staged review, never SQL
   or a direct write.
3. **Facts, not text.** Imports keep names, numbers, dates and a source address. They never store a page's own text, and
   summaries are brief and paraphrased.

---

# Part II. The features

## 4. Explore

**Built.**

**Synopsis.** Rail → Explore, or `g e`. State is in the address (`?v=`).

**Description.** Explore draws the result of a search as a graph, and grows it from there.

*Search.* The search bar builds a **pattern**: a chain of steps, each a type with conditions,
joined by relationships written `REL →` or `← REL`. Conditions are a property, an operator and a value:

| Field type | Operators |
| --- | --- |
| text, choice, list | `=` (case-insensitive), `contains`, `starts with`, `ends with`. A list matches when any value does |
| number, date | `=`, `>`, `>=`, `<`, `<=` |
| boolean | `=` |

Typing plain text searches every record's name and values. Suggestions follow the step you are on and draw values from the
data. Typing `graph` shows the model itself (types as nodes, relationships as links); *Show all* on a type brings in its records.

*Ask.* The Ask box under the search bar (`a`) turns a question of up to 300 characters into a pattern via an LLM call and puts it in the search
bar, where it can be read and edited. Only the question and the model's type, field and relationship names are sent out. The
answer is checked by `PatternChecker`: every type, field and relationship must exist in the model, in that direction; a value
must appear in the question; at most 5 steps, 6 conditions on a step and 100 characters in a value. A failure says why and
searches nothing.

*Canvas.* Click selects and fills the details panel. Right-click expands (all links, or one relationship), removes, removes
all others, or opens the record. Double-click expands everything linked. Expanding adds at most 100 nodes at a time, with *Show
more*. Nodes already drawn never move when others are added, and the zoom is kept; dragging pulls linked nodes after it,
like springs. *When expanding, only show* limits expansion by relationship or property. *Show values on links* labels links
with their values (`WORKS_ON · Lead · 3`). Models can give types a colour, an icon and a caption.

*Table.* `t` swaps the canvas for a table of the same nodes and links; selection is shared.

*Totals.* `s` shows count, sum, minimum, maximum and average of each number field of the search's matches.

*Export.* Nodes (CSV), links (CSV), both (JSON), picture (PNG).

*Find a path* (`w`). The shortest chain of links, in either direction, between the selected record and another, up to 6 links.
It stops looking through a hub that is too busy and says so.

*What changed nearby* (`d`). Edits since a date to the record, the records it links to and their links, up to 500.

*Open and edit* (`o`). Opens the selected record's own page.

*Link history.* Selecting a link lists its changes under its values.

*As of.* A date or slider hides records and links that did not exist on that day and shows the rest with the
values they had, rewound from the audit trail.

**Limits.** Saved canvases keep at most 150 nodes in an address; beyond that only the pattern is kept and the first page of
matches reopens. An address over 8,000 characters is ignored.

**Not yet.** As of cannot show a record deleted since, and a link made before history was kept looks as if it always existed.
Totals cannot be saved apart from their search. No chart views.

**See also.** [Saved views](#12-saved-views-and-permalinks), [Quick-find](#14-quick-find), `Core/Explore`, `Core/Graph`,
`Core/Ask`, `Core/Paths`, `wwwroot/js/graph.js`.

## 5. Lists and record pages

**Built.**

**Synopsis.** One rail entry per type, `/data/<Type>` and `/data/<Type>/<key>`. Nothing to write: generated from the model.

**Description.** *The list* shows 50 records a page with up to six fields, a search over name and values, sorting by any
heading (blank values last) and a filter per field: contains for text and lists, one value for choices and booleans, from/to for
numbers and dates. State is in the address, so it can be saved and shared.

*The record page* shows every value, every link grouped by relationship in either direction (25 shown, with a count), the
record's ids in other systems, and its [history](#11-history-and-revert).

*Forms* have a box per field checked against its type. Lists and multiple values take one per line. A new record's key is made
from its name unless given, and cannot change afterwards. A record can **create the records it requires as it is made**
("New Route" opens the same form for it, recursively); nothing saves until the outer form does, and then all of it saves in one
transaction. *Add link* offers only the relationships the model has, asks for the link's own values, and can create the other
end (saved at once).

*Rules enforced on every write:* required fields and required links; `max` links per relationship; unique key; link values valid;
a record's last link of a required kind cannot be removed; a record with links cannot be deleted (no cascade); fields and
types a domain module owns cannot be edited, added or deleted by hand.

*Hierarchies.* A type can be a tree (countries → regions → areas): a choice field says the level, and each level says what may
sit in it. A record form has a **Parent** box. Cycles, wrong levels and a second parent are refused, with the reason, in forms,
links, level edits, loading and import commits. To move a record, remove its link and add the new one.

*Name templates* make a name from fields (`{name_first} {name_last}`), so people are not asked for one.

**Limits.** A name over about 2,000 characters cannot be stored. A reason note is 500 characters.

**Not yet.** No bulk edit. No way to edit a model's types, fields or relationships in the app (a model file or *Start from CSVs*
makes them). Phone-width layouts have not been checked.

**See also.** [models](models.md#lists-and-records), [hierarchies](models.md#hierarchies), `Core/Records`.

## 6. Importing: CSV file

**Built.** Partial for updates.

**Synopsis.** Import → CSV file.

**Description.** A file is **records of one type** or **links between records already here**. The app reads a preview (first
rows), guesses the type from the file name, keys and columns that look like the type's fields, guesses the key column (`id`,
`key`, `code`, `<type>_id`), and guesses link ends from column names (`stop_id`, `from_stop_id`), swapping them if the model has
the relationship the other way. It accepts comma or semicolon files up to 100 MB. Field types are guessed from the first 1,000
rows, and a new type or new fields join the model.

With no column that looks like a key, **No key column** identifies each row by its name instead (which must then have a
column): matched against existing records of the type ignoring case, and otherwise keyed from the name's slug the same way
a hand-added record is (`zoe-adams`), disambiguated if it collides. A name matching more than one existing record is added
as new, with a note.

*Check* reports rows to add, rows skipped and why (no key, repeated key or name, a link to a missing record, already here), and
stops on a value that does not fit its field. A required field needs a column; a type that requires links cannot be imported
this way. List values split on `|`.

*Update records that are already here* gives the matched record the file's values where they differ. An empty cell never
clears a value, and with no name column the name stays. The review lists each change as `city: Bern → Zurich`, and commit writes
one history entry per field, so the values can be put back. It needs history on, and cannot touch types a module owns.

*Update links that are already here*, offered when the relationship carries values, does the same for a link's values, but
only when the two records are linked that way exactly once; an ambiguous pair (none yet, or more than one already) is left
alone and the row becomes a further link, same as unticked.

*Review first* stages the batch (`staged_item`); the batch survives a reload, and is listed under Recent imports and on Home.
Untick items, then Commit or Discard. Commit is one transaction and skips anything that became a duplicate or lost an end.

*Undo* (Recent imports) removes what the import added and puts back values it changed, records and links alike. It leaves,
and lists, a record edited since, one something now links to, one a staying record needs through a required link, and a
value changed again since. The import stays committed; undo can be repeated.

**Not yet.** Read text still skips what exists, for both records and links. A link's own columns (both ends, and Start from
CSVs) still need a real key column on both sides; a link to a record identified by name, rather than a key, isn't resolved yet.

**See also.** [models](models.md#importing-a-csv-file), `Core/CsvImport`, `Core/Staging`.

## 7. Importing: Start from CSVs

**Built.** Partial: link values and hierarchies need the model editor afterwards.

**Synopsis.** Import → Start from CSVs.

**Description.** Choose all of a customer's files. Each becomes a type named from the file (`clients.csv` → `Client`, `people.csv` →
`Person`) with a key, a name and a typed field per other column. A column naming another file's type is a link (`HAS_CLIENT`),
not a field; a file of two such columns and no key of its own is links. Every guess can be changed before *Check*, which writes
nothing. *Create the model* then imports each file's records, then the links, one import each, so each can be undone. Types
already in the model are added to.

**Not yet.** Link values and hierarchies come from a model file or [the model editor](#8-the-model-editor) only.

## 8. The model editor

**Built.** Partial: no bulk edit, and a type can't be renamed.

**Synopsis.** Model, in the rail (`/model`), keys `t` and `r` for its tabs.

**Description.** Changes the generic store's types, fields, relationships and link values from the app, so a model needs no
developer once it is started. Every change is checked against the records and links already there — a choice option still
in use can't be taken away, a field or relationship can't be made required until everything already has a value, a type or
relationship with records or links in it can't be deleted — and is refused with the reason when it wouldn't fit. Each is one
transaction that also writes the [history](#11-history-and-revert), so it can be read back under a `type:`, `field:`,
`relationship:` or `attribute:` entry. A type or field a domain module derives can't be touched at all; one it only reads or
writes, such as a required link a module relies on, can be added to but not taken away or changed underneath it.

*Types* tab. **Add type** names a new type, with an optional colour and icon. Each type lists its fields, with buttons to add
one, edit it (description, group, required, a choice's options), rename it (its values move with it), change its type (Text
to a number, date, yes/no or choice and back, checked against the values already there), or delete it (asks again, and
offers to discard the values, if any record still has one). **Hierarchy** sets or clears how the type's records form a tree:
a required choice field says the level, and a non-required relationship from the type to itself says the parent; the levels
always cover the field's options exactly, with one at the top and a way down to every other. Deleting a type only works
once it has no records and is in no relationship.

*Relationships* tab. **Add relationship** names two types and a relationship between them, with required and a max optional.
Required and max are checked against the links already there, the same way [a model file's are](models.md#the-model-file).
Each relationship lists its values ([link attributes](models.md#link-attributes)), edited the same way as a type's fields. A
relationship can be renamed (its links keep their ends and values) or deleted, which only works with no links in it.

**Not yet.** Renaming a type: a node's id embeds its type in lower case, so every record and reference to it would need
rewriting. Bulk-editing many records' values at once, beyond what a CSV update does. Groups, descriptions and limits for a
relationship's values only come through here or a model file, same as a type's fields.

**See also.** [models](models.md#editing-a-model-in-the-app), `Core/Models/ModelEditor.cs`.

## 9. Importing: Read text

**Built.** Partial: it fills text fields and text lists, not `multiple` numbers, dates or choices.

**Synopsis.** Import → Read text. Hidden when `Extraction:Enabled` is `false`; needs a model provider: `Anthropic:ApiKey`, or `Llm:*` with `Llm:Provider` set to `openai`.

**Description.** Paste text, or give an address to fetch (up to 5 MB, refused by sites that block it; pasting is more reliable and
the right way for sites that forbid scraping). The model's types, fields, relationships and link values become the extraction
tool's schema, so the answer from the LLM can only name what the model has. Then `ModelExtractionValidator` drops any name or value not in the
source text, or over 200 characters, and a domain's `CheckExtracted` drops what it can tell is wrong. A type that requires links
is kept only when the text gives them. Records already here, matched by key or exact name,
are left alone but can be linked to. The rest is staged for review, committed and undone exactly as a CSV import.

Only the extracted facts and the source address are kept, in the batch, never the page's text.

**Limits.** Long text is read in pieces of at most 24,000 characters. Hierarchies are checked when you commit.

## 10. Home, Timeline and Cleanup

**Built.** Phone layout not checked.

**Home** (`/`). Database status first (unreachable, or migrations pending, with what to run). Then pinned views; a chip per type
with its record count; every relationship with its link count; recent records (added or edited); recent changes with reasons;
recent imports (a staged one links to its review); records added per month. A model can hide `records`, `changes`, `imports` or
`growth` with `Models:<key>:Home:Hide`. Recent records and changes come from the audit trail, so are empty where history is off.

**Timeline** (`/timeline`). The records of a type laid out by one of its single-valued date fields, newest first (300 at a time),
with a bar per month (up to 600 months). Selecting a month narrows the list.

**Cleanup** (`/cleanup`, keys `d` `u` `m`, `r` to look again). Three finders: names that match apart from case and spacing; records
with no links; records missing a required value or link. 50 groups and 200 items are shown. Types a module owns are left out.
**Merge** keeps the chosen record's values, fills its gaps from the others, moves their links and external ids to it, then deletes
them, in one transaction with a history entry saying what went where. It is refused, changing nothing, if the result would break
a rule (a `max`, a hierarchy).

**Not yet.** Fuzzy duplicates (spelling variants) are not found. Merge cannot be reverted.

## 11. History and revert

**Built.** Partial: see below.

**Synopsis.** A record page's History list; Explore's link details; the reason box on every save.

**Description.** The audit trail is three tables: `justification` (why: kind `manual`, `import`, `derived` or `correction`; a brief
note; an optional source with an id, a kind and a comment), `change_set` (one save, one import commit or one rebuild) and
`change` (one row per field changed, link or record created or deleted, with old and new values). Deleting writes the whole old
record with its links. An import's creates are implied by the batch rather than written row by row, so a 45-million-link import
doesn't write 45 million history rows; its updates and later edits are.

*Reasons.* A save, delete or link asks for a reason (a line in your own words; up to 500 characters) and an optional source. The
model file can make the reason optional (`history.requireReason: false`) or turn history off (`history.record: false`), and lists
the source kinds on offer (`history.sourceKinds`, `Internet` if left out).

*Revert.* Each manual edit in a record's History has **Revert**. It reverts a field edit, a link added, removed or edited, a record
created (if nothing was done to it) and a record deleted (with its values and the links its type requires). It is itself an edit,
logged as "Reverted change #n", and runs the module's hooks. It refuses, with the reason, if the value changed again, the link is
gone or the record was edited since.

*Derived changes.* A module rebuilding derived records (climbing's trips) logs one `derived` change set pointing at its cause.

**Not yet.** Reverting an import, a correction, a derived change, or one change that touched several records (undo an import from
Recent imports). Reverting a delete has no button, because a deleted record has no page. Import undo still judges "edited
since" by `updated_at`, and notices no link edits.

**See also.** [audit-history-plan](history/audit-history-plan.md), `Core/History`.

## 12. Saved views and permalinks

**Built.** Partial: no read-only share links.

A page's state is JSON in its address (`?v=`, URL-safe, at most 8,000 characters). Explore keeps its pattern, canvas, expansion
filters, and table and totals switches; a record list keeps its search, sort and filters. **Copy link** and the address bar both
give the same view. A **saved view** (`v`) is that state under a name (up to 80 characters); saving under an existing name
replaces it. Pinned views show on Home. Saving an Explore view can also tick *Keep nodes exactly where they are*, which adds
each node's canvas position to the state, so reopening it places nodes back exactly where they were instead of laying them out
fresh. Whatever the state names is checked against the model before use, so a stale or tampered address can only fail to open.

**Not yet.** Sharing a view as read-only waits for sign-in.

## 13. Ask (plain-English questions)

Described with Explore in [chapter 4](#4-explore). In short: question in, checked pattern out, shown and editable. **Built.**
Disabled by `Extraction:Enabled: false`. `Ask:Fixture` answers every question from a file, for tests and demos.

## 14. Quick-find

**Built.** Partial: the only action is adding.

**Ctrl K**, on any page and inside any field. Type a name to find records (by name); pages and actions are matched on every word,
those starting with the text first. Choosing a record opens it, a page goes there, and **Add *Type*** opens the new-record form.
Types a module derives are not offered for adding.

## 15. The Guide

**Built.** `/guide` for every model, `/guide/climbing` for the climbing model, from `IDomainModule.GuideHref`. Written for the
people using the app; its keyboard table is generated from the same list as the `?` dialog, so they cannot disagree.

## 16. Keyboard

**Built.** Every page is drivable from the keys in [Appendix A](#appendix-a-keyboard). Keys are ignored while typing in a field
(except Quick-find and the search bar's own keys). New pages and dialogs must add their own handling and hotkeys.

## 17. The climbing domain

**Built.** The one domain module (`Domains/Climbing`), for models that list it under `Domains`.

Six types: **Area** (country → region → area), **Crag**, **Route**, **Ascent**, **Partner** and **Trip**.

- **Logbook** (`/logbook`): every ascent, newest first, sortable; filters on dates, area, crag, grade system, discipline and style;
  a summary of the hardest clean ascent per grade system, a count per discipline and per year (falls, hangdogs, seconds and top-rope
  climbs are not clean); add and edit an ascent.
- **Insights** (`/insights`): five tabs. *Trips* (biggest trips, return countries, trips per year, two-day trips), *Places*
  (return crags, once-or-five-times-plus counts per country, crags not visited for five years), *Seasons* (days per month by year,
  ascents per month by country, season split, discipline mix), *Partners* (by trips and by ascents), *On this day*.
- **Derived data** (`ClimbingRecordHook`): a route's `grade_rank` from `GradeRanking`; an ascent is named after its route; **Trips**
  are rebuilt when an ascent's date or country changes (`TripBuilder`), so trip pages are read-only and hand edits to them
  would not survive. Multi-country trips merge from `data/corrections.json`.
- **Importers** (command line): UKC export and Mountain Project ticks, one undoable batch each, re-runnable.
  Crags and routes can also be added from text through Read text, like any model's records.
- **Views**: `climbing_*` views serve Logbook and Insights.

**Not yet.** The real climbing model's areas have no hierarchy rules. The UKC and Mountain Project importers write areas
themselves and are not checked against hierarchy rules. Phone-width Logbook and quick one-handed logging.

## 18. Command line

**Built.** Run as `dotnet run --project src/Linkbase --launch-profile http -- <command>`, or as arguments to the image.

| Command | Does |
| --- | --- |
| `model list` | Lists the configured models |
| `model migrate` | Applies migrations to every configured model's database, including the real one |
| `model load <file> [model]` | Loads a model file, replacing everything generic in that model in one transaction |
| `model security on\|off [model]` | Turns row security on or off, as the database owner. `on` needs a `security` section |
| `climbing import <model> ukc\|mp\|all [--fresh] [--same-database]` | Loads the export files, one batch each, and rebuilds trips |

Migrations are never part of start-up.

---

# Part III. Model file

A model file is JSON (comments allowed) beside its CSVs. [models](models.md#the-model-file) has every setting with examples;
the shape is:

```
model
├── name, folder, history { record, requireReason, sourceKinds }
├── types[]  name, color, icon, owner, nameTemplate, source { file, key, name }, hierarchy { by, via, levels[] }
│   └── fields[]  key, type, column, required, options, multiple, description, group, owner
└── relationships[]  from, type, to, required, max, source { file, fromColumn, toColumn }
    └── fields[]  (link values) key, type, column, required, options
```

Loading is strict: a row missing a required value, a value that fits no field type, a choice outside its options, a record over a
`max`, a hierarchy rule broken, or a required link missing stops the load and names the rows. A row with no key is skipped,
a repeated key keeps its first row, and a link whose ends are not both loaded is left out. Loading **replaces** the model's data,
including anything added in the app, so don't reload a model people now edit.

Models are listed under `Models` in configuration: `Name`, `ConnectionString` (naming an entry under `ConnectionStrings`),
`Domains`, `Order`, `Home:Hide`. The choice of model is kept in a cookie for a year; `/model/<key>` switches directly.

---

# Part IV. Running it

- **One app, one database per model**, Postgres only. No lazy loading, no cascading deletes, no scoped `DbContext` in components.
- **Container.** The `Dockerfile` image runs under Docker, Podman, OpenShift's restricted policy (any user id, read-only root
  with `/tmp`), port 8080, JSON logs, `/health/live` and `/health/ready` (every model's database reachable and migrated).
  Examples for Kubernetes, OpenShift and Argo CD are in [`deploy/`](../deploy/README.md).
- **Configuration** is by environment variable, listed in [operations](operations.md#configuration).
- **Backups** are `scripts/backup-db.ps1`; restoring, rebuilding and rehearsing are in [operations](operations.md).
- **Tests.** Unit, database (Testcontainers on real Postgres), bUnit page tests and Playwright browser tests, run in CI on every
  push; coverage uses an in-memory collector.
- **Security.** Single user, no sign-in: run it on one machine until [security-roadmap](security-roadmap.md) is done.
  Reading text and Ask send text to the configured model provider; records and values are never sent by Ask, and Read text sends only the text you give it.

---

# Appendix A. Keyboard

The `?` dialog is the source of truth; this is its content.

| Where | Keys |
| --- | --- |
| Anywhere | `Ctrl K` Quick-find · `/` search or first field · `g h` Home · `g e` Explore · `g 1…9` rail item · `?` help |
| Search box, empty | `← →` move between steps · `Backspace` take off the last condition · `Enter` search · `Esc` close suggestions, then leave |
| Explore | `t` table · `s` totals · `v` views · `a` Ask · `f` fit · `c` clear · `n` `p` next or previous node · `e` expand · `w` path · `d` changes nearby · `Delete` remove node · `Esc` close menu or selection |
| Cleanup | `d` `u` `m` pick a finder · `r` look again |

# Appendix B. Status at a glance

| Feature | Status | Gap |
| --- | --- | --- |
| Generic store, model files | Built | |
| Explore: patterns, canvas, table, totals, export | Built | |
| Explore: Ask | Built | |
| Explore: path, nearby changes, as of, link history | Built | Deleted records not shown as of |
| Lists and record pages, forms, hierarchies, link values | Built | No bulk edit |
| Model editor | Partial | No bulk edit; a type can't be renamed |
| Saved views, pinned layouts, permalinks | Built | No read-only share |
| Quick-find | Partial | Only "add" as an action |
| Import: CSV records and links | Built | |
| Import: update existing records | Partial | CSV only, not Read text |
| Import: Start from CSVs | Partial | No hierarchies or link values from the files; use the model editor after |
| Import: Read text | Partial | Not `multiple` numbers, dates, choices |
| Review, commit, undo | Built | Undo's "edited since" uses `updated_at` |
| Audit trail, reasons | Built | |
| Revert | Partial | One record's manual edits only |
| Home, Timeline, Cleanup, Merge | Built | No fuzzy duplicates; phone untested |
| Climbing: Logbook, Insights, trips, importers | Built | Phone logging |
| Security parts, the visibility rule, grant sources (model file or API), label columns, labels from CSV, bulk label edits, row policies, field labels and masking | Built | [security-design](security-design.md), [models](models.md#security) |
| Sign-in, per-record field labels, label UI | Not built | [security-design](security-design.md) |
| Off-site backups, per-customer hosting | Not built | [operations](operations.md#still-to-do) |
| Phone layouts | Not built | |

# Appendix C. Limits

| Thing | Limit |
| --- | --- |
| Question to Ask | 300 characters; 5 steps; 6 conditions per step; values 100 characters |
| Names/values from Read text | 200 characters; read in pieces of 24,000; fetched page 5 MB |
| CSV file | 100 MB; types guessed from the first 1,000 rows |
| Explore expansion | 100 nodes at a time |
| Path | 6 links by default |
| Nearby changes | 500 entries |
| Address state | 8,000 characters; 150 nodes in a saved canvas; view names 80 characters |
| Record list | 50 per page; 25 links per group on a record page |
| Timeline | 300 entries; 600 months |
| Cleanup | 50 groups; 200 items |
| Choice options | 50 per field |
| Reason or source note | 500 characters |
| Field description | 300 characters; group name 40 |
| Name | about 2,000 characters |

# Appendix D. Glossary

**Audit trail.** The log of every change and why. **Batch.** One import, staged or committed. **Derived.** Worked out by a
module, not typed in. **Domain module.** Code that adds pages, hooks and importers to some models. **Edge.** A link.
**Hook.** A module's code that runs when records of chosen types are saved. **Key.** A record's identifier within its type.
**Model.** One self-contained database of types, records and links. **Node.** A record. **Pattern.** The chain of typed, filtered
steps that Explore searches for. **Relationship.** A named, directed kind of link between two types.
**Staged.** Waiting in review, not yet written. **View.** A saved page state, pinnable to Home; an Explore view can also pin its
canvas's node positions.

# Appendix E. Where each feature lives in the code

| Feature | Folder |
| --- | --- |
| Explore, Ask, path, as of | `Core/Explore`, `Core/Ask`, `Core/Graph`, `Core/Paths`, `wwwroot/js/graph.js` |
| Lists, records, forms, merge, hierarchy | `Core/Records` |
| Import hub, CSV, Start from CSVs | `Core/Import`, `Core/CsvImport` |
| Read text | `Core/Extraction` |
| Review, commit, undo | `Core/Staging` |
| Audit trail, revert | `Core/History`, `Data/HistoryEntities.cs` |
| Saved views | `Core/Views` |
| Home, Timeline, Cleanup | `Components/Pages/Home.razor`, `Core/Home`, `Core/Timeline`, `Core/Cleanup` |
| Quick-find, hotkeys, guide | `Components/Layout`, `Components/Guide`, `Components/Pages/UserGuide.razor` |
| Model files, catalog, switcher | `Core/Models` |
| Climbing | `Domains/Climbing` |
| Schema and migration | `Data`, [schema](schema.md) |

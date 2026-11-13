# Models

A model is one set of data and the types it's made of, such as a climbing logbook, a rail network or a consultancy's people
and projects. Each model has its own database, and the rail's switcher moves between them.

There are two kinds:

- **Generic models** are defined by a model file and loaded with one command. Explore works on them straight away, and their
  home page lists what's in them. This is how a new model starts.
- **Domain models** also have code of their own, plugged in through `IDomainModule` in `Domains/<Domain>/`: pages,
  imports and logic that a model file can't express, such as climbing's grades and trips. Climbing is the only one.

## Trying it

Locally, four models are configured in `appsettings.Development.json`: your climbing logbook, the example consultancy, Swiss
public transport and the demo climbing logbook. The last three live in the `linkbase-samples` container (see
[performance](performance.md)). Hover over the rail and use the switcher by the model's name.

## The demo climbing logbook

`models/climbing-demo/` is a made-up logbook to try the app with, or to show it, without anyone's real data: 45 areas, 39 crags,
442 routes, 1,555 ascents over 176 trips (2016 to 2025) and 20 partners, all invented, in the shape of
`models/climbing/model.json`. Its CSV files are made by `generate.py` with a fixed seed, so running it again gives the same
files. To load it, make a database and load the model file like any other (see [Adding a model](#adding-a-model)):

```powershell
docker exec linkbase-samples psql -U linkbase -d postgres -c "CREATE DATABASE climbing_demo"
dotnet ef database update --project src/Linkbase --connection "Host=127.0.0.1;Port=55433;Database=climbing_demo;Username=linkbase"
dotnet run --project src/Linkbase --launch-profile http -- model load models/climbing-demo/model.json climbing-demo
```

Its route grade ranks and trips are written into the files, since hooks don't run when a model file is loaded. Reloading
replaces everything, edits included.

## The model of this code base

`models/linkbase-code/` maps Linkbase itself: folders, source files, C# types (and Razor components) and docs, with who is
defined where, who implements what, which test class tests which type, which files use a type and what the docs mention.
`generate.py` makes the CSV files from `git ls-files`, so run it from inside the repo and reload to bring the model up to date.
Links are found by name, not by compiling, so a type is only linked when its name is unique and at least four letters long.
It is a map to explore, not proof of what depends on what. History is off for it, since it is rebuilt from git. Load it like
any other model (see [Adding a model](#adding-a-model)), with `model load models/linkbase-code/model.json linkbase-code`.

## Climbing in the generic store

Climbing's records are in the generic store like any other model's. Its model file is `models/climbing/model.json`, and the
Climbing module keeps the grade rank and the trips. See [the plan](history/climbing-to-generic-plan.md).

## Adding a model

1. Write a model file, and put its CSV files beside it or in the folder it names. `models/consultancy/` is a small one to copy.
2. Create a database for it and apply the migrations:

   ```powershell
   docker exec linkbase-samples psql -U linkbase -d postgres -c "CREATE DATABASE my_model"
   dotnet ef database update --project src/Linkbase --connection "Host=127.0.0.1;Port=55433;Database=my_model;Username=linkbase"
   ```

3. Add it to `Models`, with a connection string by the same name (see below).
4. Load it:

   ```powershell
   dotnet run --project src/Linkbase --launch-profile http -- model load models/my-model/model.json my-model
   ```

Loading replaces everything in the model's generic store, in one transaction, so a failed load changes nothing. Node ids are
the type in lower case, a colon and the row's key, such as `stop:8503000`, so they stay the same when a model is reloaded, and
saved views still line up.

## The model file

JSON, with comments allowed. Paths are relative to the model file.

```json
{
  "name": "Example consultancy",
  "folder": "data",
  "types": [
    {
      "name": "Person",
      "color": "#c2410c",
      "icon": "Person",
      "source": { "file": "people.csv", "key": "id", "name": "name" },
      "fields": [ { "key": "title" }, { "key": "joined", "type": "date" }, { "key": "skills", "type": "list" } ]
    }
  ],
  "relationships": [
    {
      "from": "Person", "type": "WORKS_ON", "to": "Project",
      "source": { "file": "assignments.csv", "fromColumn": "person", "toColumn": "project" },
      "fields": [ { "key": "role" }, { "key": "days_a_week", "type": "number" } ]
    }
  ]
}
```

| Setting | Means |
| --- | --- |
| `name` | What the model is called. Shown nowhere yet: the name in `Models` is what the app shows |
| `folder` | Where the CSV files are. The model file's own folder if left out |
| `types[].name` | Letters and digits, starting with a capital, such as `Stop` |
| `types[].color`, `icon` | Optional. A colour such as `#0f766e`, and a [Material icon](https://fonts.google.com/icons)'s name in PascalCase, such as `Train` |
| `types[].source` | The CSV file, the column identifying a row (`key`), and the column to show as its name (`name`, the key if left out). Leave it out for a type that starts empty, such as one a domain module fills |
| `fields[]` | `key` in lower case, digits and underscores. `type` is `text` (the default), `number`, `date`, `boolean`, `choice` or `list`. A choice lists its values in `options` (up to 50), and a value that isn't one of them, spelled exactly, stops the load. A list is several text values in one cell, split by `|`, such as `Figma|User research`. `column` is the CSV column, the key if left out |
| `fields[].description` | Optional, up to 300 characters. Shown beside the field on its form and on the record page, so people know what to put in it |
| `fields[].group` | Optional, up to 40 characters. Fields of a type with the same group are shown together under its heading, such as `Name` over a first and last name. Display only: each stays a separate field in the CSV, filters and history. Not for a relationship's fields |
| `fields[].multiple` | Optional. The field holds several values of its type, such as a list of dates. In a CSV they're split by `\|`, on a form they're one per line (or a multi-select for a choice), and each is checked on its own. A filter matches when any value does. `"type": "list"` is the same as a text field with `multiple`. Not for booleans |
| `types[].nameTemplate` | Optional. Makes each record's name from its fields, such as `"{name_first} {name_last}"`. A field with no value drops out and so does the space around it. People then aren't asked for a name, and it follows the fields when they're edited. Each `{field}` must be a field of the type with one value that people fill in. The source's `name` can hold the template instead, if it has braces |
| `relationships[].max` | Optional. The most links of this kind one record of the `from` type may have, such as `1` for a person's team. With `required: true` that means exactly one. The load stops at a record over it, and the Add link and new record forms refuse a link over it |
| `types[].owner`, `types[].fields[].owner` | Optional. The domain module that writes these records or keeps this value, by its key, such as `Climbing`. See [owned by a module](#owned-by-a-module) |
| `history` | Optional. `record` (default `true`) keeps an audit trail of changes, and `requireReason` (default `true`) means a change can't be saved without a reason. See [history](#history) |
| `types[].hierarchy` | Optional. Makes the type's records a tree, with rules about what may be in what. See [hierarchies](#hierarchies) |
| `relationships[].type` | Capitals, digits and underscores, such as `CALLS_AT` |
| `relationships[].source` | The CSV file, and the columns holding the keys of the two ends. They can be the types' own files. Leave it out for a relationship that starts with no links |
| `relationships[].fields` | The values a link of this kind carries, written like a type's `fields`, such as a person's `role` on a project. See [link attributes](#link-attributes). Not `owner` |
| `required` | On a field, `true` means every record needs a value. On a relationship, it means every record of the `from` type needs at least one of its links, such as each ascent's route |

In Explore, a filter on a list matches when any of its values does, so `skills = postgres` finds everyone with Postgres
among their skills.

The example consultancy (`models/consultancy/model.json`) shows `description`, `group`, `multiple`, `nameTemplate` and `max` in use: a
person's name is made from two grouped fields, and a person is in at most one team.

A row without a value for a required field stops the load, and so does a record left without a required relationship's
link, once every link is in. Both say which rows or records.

While loading, a row with an empty key is skipped, a repeated key keeps its first row, and a link whose ends aren't both
loaded is left out. A value that doesn't fit its field's type stops the load and says which field.

### History

Every model keeps an audit trail unless its model file turns it off: what changed, when, and why. Each edit, delete, link
change and import commit saves a reason with it (a short note in your own words, and optionally a web address where it can
be checked), and the record's page shows its history, newest first.

```json
{ "name": "Swiss rail", "history": { "record": false } }
{ "name": "Projects", "history": { "requireReason": false } }
```

- `record: false` keeps no history. It can't be combined with `requireReason: true`, so the load says so.
- `requireReason: false` makes the reason box optional. A reason given is still kept.
- `sourceKinds` lists the kinds of source a change can name, such as `["Internet", "Jira", "SharePoint"]`. A change can carry a source: its kind, an id (a web address or whatever identifies it there) and a brief comment. `Internet` alone if left out.
- The settings are saved with the model each time it is loaded, so a file that leaves `history` out turns both back on.
- Turning history off keeps what was already recorded, and so does a reload.
- An import is one entry for the whole batch, with its source. Its records show "Added by import" at the start of their
  history, and only later edits, deletes and link changes are listed one by one.
- A domain module's rebuild, such as climbing's trips, is one entry of kind Derived listing the records that came, went
  or differed.

See [audit-history-plan](audit-history-plan.md) for the design.

### Owned by a module

Some data is worked out by a domain module rather than typed in, such as a route's grade rank or the trips made from a
logbook's ascents. The model file says so with `owner`:

```json
{ "name": "Route", "fields": [ { "key": "grade" }, { "key": "grade_rank", "type": "number", "owner": "Climbing" } ] },
{ "name": "Trip", "owner": "Climbing", "fields": [ { "key": "country" } ] }
```

- **A kept field** is shown on the record page, marked "kept by Climbing", but the edit form has no box for it and the CSV
  import won't take a column for it. Saving keeps its value, and the module's hook sets it.
- **A derived type**'s records are shown like any others, marked as derived, but can't be added, edited, deleted or imported.
  Links to and from them are the module's too: the record page offers none to add or remove, and the CSV import refuses
  them. Deleting a record, or undoing the import that added it, takes its links to derived records with it.

The loader loads owned data from the CSV files like any other, since loading replaces everything anyway. An owner must be
one of the app's domains, and only a type's fields can have one. A kept field that's required needs no column in a CSV
import, since its module fills it in, and a relationship to a derived type can't be required, as nobody could add the
records that need it.

**Hooks.** A module can register an `IRecordHook` in its `AddServices`, naming the types it watches. It runs only for models
that list the module under `Domains`:

- `BeforeSave` runs before a record of a watched type is saved by the edit form or the CSV import, in the same transaction,
  and sets the fields the module keeps.
- `AfterChangeAsync` runs once the changes are saved, with every change one action made to the watched types: records
  created, updated or deleted, with their values before and after, and links added or removed, once for each end. It's
  for keeping derived records up to date.

Climbing's hook (`ClimbingRecordHook`) is the working example: it sets a route's `grade_rank` before it's saved, names each
ascent after its route, and rebuilds the `Trip` records, with `GenericTripService`, when an ascent's date or place changes.

Hooks don't run when a model file is loaded. A module writes its derived records with `RecordRepository.ReplaceDerivedAsync`,
which replaces all of a type's records and their links in one transaction, keeps the relationships' counts, and runs no
hooks.

## Hierarchies

Records of one type can form a tree, with rules about which level may sit in which. The demo climbing logbook's `Area` is the
working example (`models/climbing-demo/model.json`): countries at the top, regions in countries, and areas in regions or in
other areas.

```json
{
  "name": "Area",
  "fields": [ { "key": "kind", "type": "choice", "options": [ "country", "region", "area" ], "required": true } ],
  "hierarchy": {
    "by": "kind",
    "via": "IN",
    "levels": [
      { "kind": "country", "root": true },
      { "kind": "region", "under": [ "country" ] },
      { "kind": "area", "under": [ "region", "area" ] }
    ]
  }
}
```

| Setting | Means |
| --- | --- |
| `by` | The field saying which level a record is on. It must be a required `choice` field of the type |
| `via` | The relationship from a record to its parent. It must be one the model has from the type to itself, and can't be `required`, because the top has no parent |
| `levels[].kind` | One of the `by` field's options. Every option needs a level |
| `root` | A record at this level has no parent. At least one level must be a root |
| `under` | The levels a record at this level may be in. A level that isn't a root needs some, and every level has to be reachable from a root. A level may list itself, which is how areas nest to any depth |

The rules, wherever a record or a link is made:

- A record at a root level has no parent, and any other record is in at most one record of a level its own lists.
- The tree can't go round in a circle, and a record can't be in itself.
- **Loading** a model file checks all of it, including that every record below the top has a parent, and says which records
  break which rule.
- **The new record form** has a Parent box for the type. It's optional there, because the level chosen decides whether one
  is needed. The save is refused if a record below the top has none, or if the parent's level doesn't suit.
- **Add link** checks the same, and says to remove the existing link first if the record is in something already. That is
  also how to move a record: remove the link, then add the new one. Removing a parent is allowed, since a move needs it.
- **Editing a record's level** is refused if it wouldn't suit the record it's in, or the records in it.
- **Importing** (a CSV file, or reading text) checks each link it adds when you commit, and a link that breaks a rule stops
  the whole commit, which changes nothing, and says which records. The batch stays in review so those links can be
  unticked. An import of records alone isn't checked for parents, because it adds no links; add them afterwards.
  Something already wrong in the model doesn't stop an import that doesn't touch it.

Not covered: climbing's UKC and Mountain Project importers write their areas themselves and aren't checked. They build
country, region, then area, which the demo's levels allow. The real climbing model (`models/climbing/model.json`) has no
hierarchy defined yet. Hierarchies are set in the model file only, never by an import.

## Configuring models

```json
"Models": {
  "climbing": { "Name": "Climbing Logbook", "ConnectionString": "Logbook", "Domains": [ "Climbing" ], "Order": 1 },
  "consultancy": { "Name": "Example consultancy", "ConnectionString": "Consultancy", "Order": 2 }
},
"ConnectionStrings": {
  "Consultancy": "Host=127.0.0.1;Port=55433;Database=consultancy;Username=linkbase"
}
```

- **ConnectionString** names an entry under `ConnectionStrings`. A connection string with a password belongs in the git-ignored
  `appsettings.Development.local.json` or an environment variable, never in a committed settings file. See [operations](operations.md#secrets).
- **Domains** lists the domain modules a model uses, by key. Leave it out for a generic model.
- **Order** sets the order in the switcher. The first model is the default.
- **Home:Hide** lists the home page's panels this model leaves out: `records`, `changes`, `imports` or `growth`, as in
  `"Home": { "Hide": [ "growth" ] }`. Growth counts every record, so a model with millions may prefer to hide it. Recent records and
  changes come from the audit trail, so they are empty where history is off.
- **With no `Models` section**, there's one model, using `ConnectionStrings:Linkbase` and every domain. **With one model**, the
  switcher is hidden. That's how a customer's instance should be set up: their model only.

The choice is kept in a cookie for a year, and switching reloads the page. `/model/<key>` switches directly.

## Commands

| Command | Does |
| --- | --- |
| `model list` | Lists the configured models |
| `model migrate` | Applies migrations to every configured model's database, **including your real one** |
| `model load <file> [model]` | Loads a model file into a model, the first one if none is named |
| `climbing import <model> ukc\|mp\|all [--fresh] [--same-database]` | Loads the UKC and Mountain Project exports into `<model>`, one undoable batch per file, and rebuilds the trips. `--fresh` first reloads the model file, which empties the model's records, and needs `--same-database` for the climbing model's own database |

Run them with `dotnet run --project src/Linkbase --launch-profile http -- <command>`. The launch profile matters: without it,
`appsettings.Development.json` and its models aren't read.

## Importing a CSV file

**Import** in the rail opens one page for bringing data in: **CSV file** here, **Read text** (below), and any import a domain
adds. The CSV tab works without writing a model file. Each file is one of two things:

- **Records of one type**, new or already in the model. Choose the column that identifies each row (the key) and the one that
  names it, then which other columns to keep, each as a field with a type. The types are guessed from the first 1,000 rows. A
  new type, and new fields on a type that's already there, join the model.
- **Links between records already here**, such as which supplier supplies which project: the type and key column at each end,
  and the relationship's name. Import the records first.

A type's required fields each need a column, and a row with nothing in one stops the import. Records of a type that requires
links can't be imported this way, because an import of records adds no links: add them in the app.

It guesses what it can from the headers and the model. A column called `stop`, `stop_id`, `Stop ID`, `stops` or `from_stop_id`
holds Stop keys when there's a Stop type, so a file with two such columns opens as links between them, swapped round if the
model has the relationship the other way. The relationship lists those the model has between the two types, filling in
the only one, and a new name can be typed. For records, it picks the existing type the file looks like, and says why under the
type: keys already there count most, then the file's name, then each column named like one of the type's fields. With no
clear winner it's a new type. A column naming another type starts unticked, as it holds keys rather than a value. The key
is a column called `id`, `key`, `code` or after the type, such as `supplier_id`. The first five rows are shown under the
file, to check the choices against.

**Check** says how many rows would be added, and why any are skipped: no key, a key repeated in the file, a link to a
record that isn't there, or one that's already here. A value that doesn't fit its field's type stops the import, and the
first ten are listed. A list field's values are split by `|`, as in a model file. A record whose key is already here is left as it is, unless **Update records that are already here** is ticked (see below).

To try it, `models/consultancy/import-demo/` and `models/swiss-rail/import-demo/` have files and a README each, saying what to
choose and what Check should say.

**Import** adds everything at once. **Review first** stages it instead: nothing changes, and the batch appears under
**Recent imports** with a Review link, so a review survives a reload. The review page (`/import/review/<id>`) lists what
would be added, a page at a time, with a box on each item. Untick any to leave them out (or Exclude all, then tick the few
you want), then **Commit** adds the rest, or **Discard** drops the lot. Staging is not CSV-specific: `ImportStagingService`
(`Core/Staging`) takes any importer's records, links and model changes, and holds them in `staged_item` until they're
committed. Committing is one transaction, and skips a record or link that has become a duplicate, or whose ends have gone,
since it was staged. Things already here are never staged, only counted.

**Updating.** With that box ticked, a record whose key is already here takes the file's values where they differ. Only what the
file has changes: an empty cell leaves the value alone, and with no name column the record keeps its name. Check counts the
records it would update, the review lists each one by what changes (`city: Bern → Zurich`), and commit writes one field-level
entry per change to the audit trail, under the import. It needs the model to keep a history, because that is what puts the values
back, and it can't update a type a domain module keeps up to date. Only CSV records update so far: links and Read text still skip
what is already here.

Undoing an import also puts back the values it changed. A value someone has changed again since stays as it is and is listed
with the records that stay. A value already back at its old value is skipped, so an undo that left something can be run again.

Undoing an import removes what it added, except a record edited since (its `updated_at` differs from `created_at`), one
something has been linked to since, and one that a record which stays needs through a link its type requires. They're
listed, the import stays committed, and it can be undone again once they're dealt with.

## Starting a model from CSV files

The **Start from CSVs** tab of the Import page builds a model from several files at once, so a new customer needs no model
file. Choose all the files, and it guesses from their names and headers:

- Each file is a type, named from the file (`clients.csv` is `Client`, `people.csv` is `Person`), with a key column
  (`id`, `key`, `code` or the type's id), a name column (the first with "name" in it) and a field for each other column, typed
  from its values.
- A column holding another file's type, such as `client_id` in `projects.csv`, is a link from each project to its client, named
  `HAS_CLIENT`, not a field. A second column to the same type is named after its header.
- A file with columns for two types and no key of its own, such as `assignments.csv` with `person` and `project`, is links
  between them. Its other columns aren't kept, since link values are defined in a model file only.

Everything guessed can be corrected before **Check**, which reports problems in any file and changes nothing. **Create the
model** then imports each file's records, and then the links, through the same service as the CSV tab, so each file is an
import of its own in **Recent imports** and can be undone there. Records are checked before anything is written; a problem
found only in the links is reported once the records are in. Types already in the model are added to, as in the CSV tab.
Editing the model afterwards, in the app, isn't built yet.

## Reading text

**Read text** finds records and links in pasted text or a fetched page, using the current model. The model's types, fields and
relationships become the extraction tool's schema, so the answer can only name what the model has. Every name and value is
then checked against the source text, and anything not in it, or longer than 200 characters, is dropped and listed in the
review's notes. A type that requires links is only kept when the text gives them. The rest is the same as a CSV import: it is
staged for review, committed in one transaction, and can be undone from **Recent imports**. A record is keyed by the slug of its
name, and one already here, by key or by exact name, is left as it is but can be linked to.

The text is sent to Anthropic, and the tab says so. Set `Extraction:Enabled` to `false` to hide it.

Reading text fills text fields and text lists, but leaves out fields that hold several numbers, dates or choices (`multiple`), because it
can't check each value against the text yet.

Each import is one transaction, and **Recent imports** can undo it. Undoing removes the records and links it added,
including changes made to them since, and a type or relationship it added once nothing is left of it. It's refused while
something added since links to one of its records, or if it would leave a record without a link its type requires. Files
can be up to 100 MB, with commas or semicolons. The batch records the file's name and what it added, never its contents.

## Link attributes

A relationship can carry values that belong to a link rather than to either record, such as how many days a week a person
works on a project. The model file lists them under the relationship's `fields`, with the same types, `options` and
`required` as a type's, and the loader reads them from extra columns of the relationship's CSV file (`models/consultancy/assignments.csv`
has `role` and `days_a_week`). They're kept in `node_link.props`, and their definitions in `link_attribute`.

- **Repeats.** The same two records can be linked the same way again only when an attribute's value differs, such as
  Dev Raman being on the portal as an Engineer and again as a Reviewer. A relationship without attributes still allows one
  link between two records. Editing a link into a copy of another is refused the same way.
- **The record page** shows each link's values as chips beside it, an edit button when the relationship has attributes, and
  asks for the values in the Add link dialog. A required attribute can't be left empty there. So can the links a new
  record's form makes, such as a required one to a Project.
- **Explore** draws a link's values on it (`WORKS_ON · Lead · 3`), lists them in the links table, and puts them in the CSV
  and JSON exports. Repeats are separate links on the canvas.
- **The CSV import** of links has a box for each of the relationship's values, which takes the column named like it by
  default. A file can fill the model's values but not define new ones. Rows that repeat a pair with the same values are
  one link, different values are two, and a row with no values counts as already there when the two are linked that way.
- **Read text** is told each relationship's values, and a value the text doesn't contain is dropped like any other. A
  link missing a required value is dropped.
- **Modules** hear `LinkUpdated` when a link's values are edited, with the values before and after. `Linked` carries the
  values it was made with, and `Unlinked` those it had.

## Lists and records

Each type in a model file gets a place in the rail, and pages generated from its definition, with nothing to write:

- **The list** (`/data/<Type>`) shows every record, 50 at a time, with up to six of its fields. Search finds any part of the
  name or a value. Clicking a heading sorts by that field (again for the other way, a third time for name order), with blank
  values last. Filters narrow it by field: text and lists contain the text, choices and booleans are one value, and numbers
  and dates have a from and a to. Values are JSON, so a filter is a lookup in `props`; if one is used often on a big model,
  give it an expression index.
- **The record** (`/data/<Type>/<key>`) shows all its values and its links, grouped by relationship and either way round. A
  group shows its first 25 links and how many there are. Explore's "Open and edit" comes here too.
- **Adding and editing** use a form with a box for each field, checked against its type. A list takes one value per line,
  and a required field can't be left empty. A new record's key is made from its name, such as `zoe-adams`, unless one is
  given, and can't change afterwards, because the record's id is made from it. A new record also chooses the records its
  required links go to, and is saved with them. Each of those can be a new record instead: "New Route" opens the same form
  for it, which can create records of its own in turn, such as a Route's Crag. Nothing is saved until the outer form is, and
  then all of it is, in one transaction, so a failure leaves none of them behind. "Add link" can create the other end too,
  but that one is saved straight away.
- **Links** can be added only where the model has a relationship, and removed one at a time, except a record's last link of
  a kind its type requires. A record that still has links can't be deleted, like anything else in the app. Its own required
  links don't count: they go with it.
- **Ids elsewhere**, such as a UKC id, are kept in `external_ref` by node id, so an import can find the record again. A
  source's id belongs to one record of a type. The record page shows them, linked when they have a web address, and they go
  when the record, its import or the model is replaced.

The home page lists how many records there are of each type and how many links of each kind, and so does the graph
model in Explore (type `graph`), on its types and links. Counting tens of millions of links takes too long to do each time,
so each relationship's count is kept in `link_definition.links`, by the loader and by adding and removing links in the app.
Anything else that writes to `node_link` must keep it up to date too.

**Loading a model file again replaces everything, including what was added or changed in the app.** Once people edit a
model's data in the app, the app is where it lives, so don't load into that model again.

## Limits
- Every model's database has climbing's tables too, empty, because there's one schema for all of them.
- Values are stored as JSON, so the rule about keeping sensitive fields in typed columns doesn't hold for generic models yet.
  See [security](security-roadmap.md).
- Climbing's rule that an ascent's name is its route's applies to any type called `Ascent`.
- Link attributes are defined in the model file, not by an import or by reading text. See
  [relationship attributes](history/relationship-attributes.md).
- A name longer than about 2,000 characters can't be stored, because names are indexed for the list's order.

# Models

A model is one set of data and the types it's made of, such as a climbing logbook, a rail network or a consultancy's people
and projects. Each model has its own database, and the rail's switcher moves between them.

There are two kinds:

- **Generic models** are defined by a model file and loaded with one command. Explore works on them straight away, and their
  home page lists what's in them. This is how a new model starts.
- **Domain models** also have code of their own, plugged in through `IDomainModule` in `Domains/<Domain>/`: pages,
  imports and logic that a model file can't express, such as climbing's grades and trips. Climbing is the only one.

## Trying it

Locally, three models are configured in `appsettings.Development.json`: your climbing logbook, the example consultancy, and
Swiss public transport. The last two live in the `linkbase-samples` container (see [performance](performance.md)). Hover over the
rail and use the switcher by the model's name.

## Climbing in the generic store

Climbing's records are in the generic store like any other model's. Its model file is `models/climbing/model.json`, and the
Climbing module keeps the grade rank and the trips. See [the plan](climbing-to-generic-plan.md).

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
saved scenes still line up.

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
| `types[].owner`, `types[].fields[].owner` | Optional. The domain module that writes these records or keeps this value, by its key, such as `Climbing`. See [owned by a module](#owned-by-a-module) |
| `relationships[].type` | Capitals, digits and underscores, such as `CALLS_AT` |
| `relationships[].source` | The CSV file, and the columns holding the keys of the two ends. They can be the types' own files. Leave it out for a relationship that starts with no links |
| `required` | On a field, `true` means every record needs a value. On a relationship, it means every record of the `from` type needs at least one of its links, such as each ascent's route |

In Explore, a filter on a list matches when any of its values does, so `skills = postgres` finds everyone with Postgres
among their skills.

A row without a value for a required field stops the load, and so does a record left without a required relationship's
link, once every link is in. Both say which rows or records.

While loading, a row with an empty key is skipped, a repeated key keeps its first row, and a link whose ends aren't both
loaded is left out. A value that doesn't fit its field's type stops the load and says which field.

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

- **ConnectionString** names an entry under `ConnectionStrings`. A connection string with a password belongs in user secrets
  or an environment variable, never in a settings file. See [operations](operations.md#secrets).
- **Domains** lists the domain modules a model uses, by key. Leave it out for a generic model.
- **Order** sets the order in the switcher. The first model is the default.
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

A model without a domain has **Import CSV** in the rail, for bringing data in without writing a model file. Each file is one
of two things:

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
first ten are listed. A list field's values are split by `|`, as in a model file. A record whose key is already here is left as it is, never updated.

To try it, `models/consultancy/import-demo/` and `models/swiss-rail/import-demo/` have files and a README each, saying what to
choose and what Check should say.

**Import** adds everything at once. **Review first** stages it instead: nothing changes, and the batch appears under
**Recent imports** with a Review link, so a review survives a reload. The review page (`/import-csv/review/<id>`) lists what
would be added, a page at a time, with a box on each item. Untick any to leave them out (or Exclude all, then tick the few
you want), then **Commit** adds the rest, or **Discard** drops the lot. Staging is not CSV-specific: `ImportStagingService`
(`Core/Staging`) takes any importer's records, links and model changes, and holds them in `staged_item` until they're
committed. Committing is one transaction, and skips a record or link that has become a duplicate, or whose ends have gone,
since it was staged. Things already here are never staged, only counted. Climbing's web import keeps its own staging until
it's moved onto this.

Each import is one transaction, and **Recent imports** can undo it. Undoing removes the records and links it added,
including changes made to them since, and a type or relationship it added once nothing is left of it. It's refused while
something added since links to one of its records, or if it would leave a record without a link its type requires. Files
can be up to 100 MB, with commas or semicolons. The batch records the file's name and what it added, never its contents.

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
- Links have no values of their own in the app yet: the ones a model file gives them show in Explore, and new links have
  none.
- A name longer than about 2,000 characters can't be stored, because names are indexed for the list's order.
